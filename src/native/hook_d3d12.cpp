// Disegno dell'overlay nei giochi DirectX 12.
// L'aggancio a Present è quello di DXGI (hook_dxgi.cpp); qui c'è solo il disegno, più la
// cattura della coda di comandi del gioco, che in DirectX 12 serve per poter disegnare.
#include "common.h"
#include <d3d12.h>
#include <dxgi1_4.h>
#include <d3dcompiler.h>
#include <cstring>

namespace
{
    template <typename T> void SafeRelease(T*& p)
    {
        if (p) { p->Release(); p = nullptr; }
    }

    // ---------------------------------------------------------------- coda di comandi del gioco

    typedef void(STDMETHODCALLTYPE* ExecuteFn)(ID3D12CommandQueue*, UINT, ID3D12CommandList* const*);
    const int kExecuteCommandLists = 10;
    ExecuteFn g_origExecute = nullptr;
    // Tutte le code "dirette" viste lavorare. Un gioco può averne molte (frame generation, ReShade,
    // mod, registratori): sullo schermo si può disegnare SOLO con quella con cui è stato creato lo
    // swapchain. Con un'altra, DirectX 12 toglie la scheda video al gioco (DXGI_ERROR_ACCESS_DENIED).
    const int kMaxQueues = 16;
    ID3D12CommandQueue* volatile g_queues[kMaxQueues] = {};

    void STDMETHODCALLTYPE HookExecute(ID3D12CommandQueue* queue, UINT count, ID3D12CommandList* const* lists)
    {
        bool known = false;
        for (int i = 0; i < kMaxQueues && !known; i++) known = g_queues[i] == queue;
        if (!known && queue->GetDesc().Type == D3D12_COMMAND_LIST_TYPE_DIRECT)
        {
            for (int i = 0; i < kMaxQueues; i++)
            {
                void* before = InterlockedCompareExchangePointer((void* volatile*)&g_queues[i], queue, nullptr);
                if (before == nullptr) { queue->AddRef(); break; }
                if (before == queue) break;   // un altro thread l'ha appena registrata
            }
        }
        g_origExecute(queue, count, lists);
    }

    IDXGISwapChain* g_foundFor = nullptr;
    ID3D12CommandQueue* g_found = nullptr;
    int g_foundCandidates = -1;

    // La coda dello swapchain. DXGI non la rivela, ma la conserva dentro l'oggetto swapchain:
    // si cerca lì dentro il puntatore di una delle code viste lavorare sullo stesso device.
    // Se non si trova e le code possibili sono più d'una, non si disegna (meglio niente overlay
    // nel gioco che un gioco caduto): resta la finestra esterna.
    ID3D12CommandQueue* FindQueue(IDXGISwapChain* sc, ID3D12Device* dev)
    {
        ID3D12CommandQueue* cand[kMaxQueues];
        int n = 0;
        for (int i = 0; i < kMaxQueues; i++)
        {
            ID3D12CommandQueue* q = g_queues[i];
            if (!q) continue;
            ID3D12Device* d = nullptr;
            if (SUCCEEDED(q->GetDevice(IID_PPV_ARGS(&d))) && d)
            {
                if (d == dev) cand[n++] = q;
                d->Release();
            }
        }
        if (sc == g_foundFor && n == g_foundCandidates) return g_found;

        ID3D12CommandQueue* found = nullptr;
        int offset = -1;
        MEMORY_BASIC_INFORMATION mbi = {};
        if (n > 0 && VirtualQuery(sc, &mbi, sizeof(mbi)) && mbi.State == MEM_COMMIT &&
            !(mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD)))
        {
            uintptr_t begin = (uintptr_t)sc;
            uintptr_t end = (uintptr_t)mbi.BaseAddress + mbi.RegionSize;
            if (end > begin + 0x1000) end = begin + 0x1000;
            for (uintptr_t p = begin; p + sizeof(void*) <= end && !found; p += sizeof(void*))
                for (int c = 0; c < n; c++)
                    if (*(void**)p == (void*)cand[c]) { found = cand[c]; offset = (int)(p - begin); break; }
        }
        if (!found && n == 1) found = cand[0];   // una sola coda possibile: è per forza quella

        g_foundFor = sc;
        g_foundCandidates = n;
        g_found = found;
        if (offset >= 0) ff::Log("DirectX 12: coda dello swapchain trovata (posizione +0x%X, %d possibili)", offset, n);
        else if (found) ff::Log("DirectX 12: una sola coda possibile, uso quella");
        else if (n > 0) ff::Log("DirectX 12: coda dello swapchain non riconosciuta tra %d: overlay nel gioco sospeso", n);
        return found;
    }

    // ---------------------------------------------------------------- risorse

    const int kMaxFrames = 16;

    ID3D12Device* g_dev = nullptr;
    ID3D12CommandQueue* g_queue = nullptr;
    ID3D12DescriptorHeap* g_rtvHeap = nullptr;
    ID3D12DescriptorHeap* g_srvHeap = nullptr;
    ID3D12CommandAllocator* g_alloc[kMaxFrames] = {};
    ID3D12GraphicsCommandList* g_list = nullptr;
    ID3D12Fence* g_fence = nullptr;
    HANDLE g_fenceEvent = nullptr;
    UINT64 g_fenceValue = 0, g_frameFence[kMaxFrames] = {};
    ID3D12RootSignature* g_root = nullptr;
    ID3D12PipelineState* g_pso = nullptr;
    DXGI_FORMAT g_psoFormat = DXGI_FORMAT_UNKNOWN;
    ID3DBlob* g_vs = nullptr;
    ID3DBlob* g_ps = nullptr;
    ID3D12Resource* g_tex = nullptr;
    // Buffer di appoggio a giro: uno non si riscrive finché la scheda video non ha finito la copia che
    // lo legge (g_uploadFence). Se sono tutti occupati, per questo frame la texture resta com'è.
    const int kUploads = 3;
    ID3D12Resource* g_upload[kUploads] = {};
    UINT64 g_uploadFence[kUploads] = {};
    // Due posti per la descrizione della texture (SRV): quando la texture cambia misura, la nuova va
    // nell'altro posto, che i comandi ancora in volo non usano più (g_slotFence).
    int g_srvSlot = 0;
    UINT64 g_slotFence[2] = {};
    UINT g_srvStep = 0;
    D3D12_PLACED_SUBRESOURCE_FOOTPRINT g_footprint = {};
    uint32_t g_texW = 0, g_texH = 0, g_texSerial = 0;
    bool g_texFresh = true;
    UINT g_rtvStep = 0;
    bool g_failed = false;

    void WaitFor(UINT64 value)
    {
        if (!g_fence || g_fence->GetCompletedValue() >= value) return;
        if (SUCCEEDED(g_fence->SetEventOnCompletion(value, g_fenceEvent)))
            WaitForSingleObject(g_fenceEvent, 500);
    }

    // Risorse da buttare che la scheda video potrebbe stare ancora usando: invece di aspettarla
    // (il gioco si fermerebbe), si mettono da parte e si liberano quando ha finito.
    struct Dead { IUnknown* obj; UINT64 value; };
    Dead g_dead[64] = {};

    int FreeDeadSlots()
    {
        int n = 0;
        for (auto& d : g_dead) if (!d.obj) n++;
        return n;
    }

    void Purge(bool all)
    {
        UINT64 done = g_fence ? g_fence->GetCompletedValue() : ~0ull;
        for (auto& d : g_dead)
            if (d.obj && (all || d.value <= done)) { d.obj->Release(); d.obj = nullptr; }
    }

    void Retire(IUnknown* obj)
    {
        if (!obj) return;
        for (auto& d : g_dead)
            if (!d.obj) { d.obj = obj; d.value = g_fenceValue; return; }
        WaitFor(g_fenceValue);   // non c'è più posto: caso raro, si aspetta
        obj->Release();
    }

    void ReleaseTexture()
    {
        Retire(g_tex);
        g_tex = nullptr;
        for (int i = 0; i < kUploads; i++)
        {
            Retire(g_upload[i]);
            g_upload[i] = nullptr;
            g_uploadFence[i] = 0;
        }
        g_texW = g_texH = 0;
    }

    void ReleaseAll()
    {
        ReleaseTexture();
        WaitFor(g_fenceValue);   // cambio di device o di coda: raro, qui si può aspettare
        Purge(true);
        SafeRelease(g_pso);
        SafeRelease(g_root);
        SafeRelease(g_vs);
        SafeRelease(g_ps);
        SafeRelease(g_list);
        for (auto*& a : g_alloc) SafeRelease(a);
        SafeRelease(g_fence);
        SafeRelease(g_rtvHeap);
        SafeRelease(g_srvHeap);
        SafeRelease(g_queue);
        SafeRelease(g_dev);
        g_psoFormat = DXGI_FORMAT_UNKNOWN;
        g_fenceValue = 0;
        memset(g_frameFence, 0, sizeof(g_frameFence));
        g_slotFence[0] = g_slotFence[1] = 0;
    }

    // Lo stesso shader di Direct3D 11 (hook_dxgi.cpp): adatta i colori ai back buffer HDR.
    const char* kShader = ff::kOverlayHlsl;

    bool CreateObjects(ID3D12Device* dev)
    {
        D3D12_DESCRIPTOR_HEAP_DESC rtv = { D3D12_DESCRIPTOR_HEAP_TYPE_RTV, kMaxFrames, D3D12_DESCRIPTOR_HEAP_FLAG_NONE, 0 };
        D3D12_DESCRIPTOR_HEAP_DESC srv = { D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, 2, D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE, 0 };
        if (FAILED(dev->CreateDescriptorHeap(&rtv, IID_PPV_ARGS(&g_rtvHeap)))) return false;
        if (FAILED(dev->CreateDescriptorHeap(&srv, IID_PPV_ARGS(&g_srvHeap)))) return false;
        g_rtvStep = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
        g_srvStep = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);

        for (auto*& a : g_alloc)
            if (FAILED(dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&a)))) return false;
        if (FAILED(dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, g_alloc[0], nullptr, IID_PPV_ARGS(&g_list)))) return false;
        g_list->Close();
        if (FAILED(dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&g_fence)))) return false;
        if (!g_fenceEvent) g_fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);

        // Una sola texture (l'overlay) letta dal pixel shader, con campionamento "a punti".
        D3D12_DESCRIPTOR_RANGE range = { D3D12_DESCRIPTOR_RANGE_TYPE_SRV, 1, 0, 0, D3D12_DESCRIPTOR_RANGE_OFFSET_APPEND };
        D3D12_ROOT_PARAMETER params[2] = {};
        params[0].ParameterType = D3D12_ROOT_PARAMETER_TYPE_DESCRIPTOR_TABLE;
        params[0].DescriptorTable.NumDescriptorRanges = 1;
        params[0].DescriptorTable.pDescriptorRanges = &range;
        params[0].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        // Costanti del colore (ColorConsts, register b0): passate direttamente nel comando, nessun buffer.
        params[1].ParameterType = D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS;
        params[1].Constants.ShaderRegister = 0;
        params[1].Constants.Num32BitValues = sizeof(ff::ColorConsts) / 4;
        params[1].ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_STATIC_SAMPLER_DESC sampler = {};
        sampler.Filter = D3D12_FILTER_MIN_MAG_MIP_POINT;
        sampler.AddressU = sampler.AddressV = sampler.AddressW = D3D12_TEXTURE_ADDRESS_MODE_CLAMP;
        sampler.ComparisonFunc = D3D12_COMPARISON_FUNC_NEVER;
        sampler.MaxLOD = D3D12_FLOAT32_MAX;
        sampler.ShaderVisibility = D3D12_SHADER_VISIBILITY_PIXEL;
        D3D12_ROOT_SIGNATURE_DESC rs = { 2, params, 1, &sampler, D3D12_ROOT_SIGNATURE_FLAG_NONE };
        ID3DBlob* blob = nullptr;
        if (FAILED(D3D12SerializeRootSignature(&rs, D3D_ROOT_SIGNATURE_VERSION_1, &blob, nullptr))) return false;
        HRESULT hr = dev->CreateRootSignature(0, blob->GetBufferPointer(), blob->GetBufferSize(), IID_PPV_ARGS(&g_root));
        blob->Release();
        if (FAILED(hr)) return false;

        return SUCCEEDED(D3DCompile(kShader, ff::kOverlayHlslSize, nullptr, nullptr, nullptr, "vs", "vs_5_0", 0, 0, &g_vs, nullptr)) &&
               SUCCEEDED(D3DCompile(kShader, ff::kOverlayHlslSize, nullptr, nullptr, nullptr, "ps", "ps_5_0", 0, 0, &g_ps, nullptr));
    }

    bool EnsurePipeline(ID3D12Device* dev, DXGI_FORMAT format)
    {
        if (g_pso && g_psoFormat == format) return true;
        WaitFor(g_fenceValue);
        SafeRelease(g_pso);

        D3D12_GRAPHICS_PIPELINE_STATE_DESC d = {};
        d.pRootSignature = g_root;
        d.VS = { g_vs->GetBufferPointer(), g_vs->GetBufferSize() };
        d.PS = { g_ps->GetBufferPointer(), g_ps->GetBufferSize() };
        d.BlendState.RenderTarget[0].BlendEnable = TRUE;
        d.BlendState.RenderTarget[0].SrcBlend = D3D12_BLEND_ONE;               // alfa premoltiplicato
        d.BlendState.RenderTarget[0].DestBlend = D3D12_BLEND_INV_SRC_ALPHA;
        d.BlendState.RenderTarget[0].BlendOp = D3D12_BLEND_OP_ADD;
        d.BlendState.RenderTarget[0].SrcBlendAlpha = D3D12_BLEND_ONE;
        d.BlendState.RenderTarget[0].DestBlendAlpha = D3D12_BLEND_INV_SRC_ALPHA;
        d.BlendState.RenderTarget[0].BlendOpAlpha = D3D12_BLEND_OP_ADD;
        d.BlendState.RenderTarget[0].RenderTargetWriteMask = D3D12_COLOR_WRITE_ENABLE_ALL;
        d.SampleMask = 0xFFFFFFFF;
        d.RasterizerState.FillMode = D3D12_FILL_MODE_SOLID;
        d.RasterizerState.CullMode = D3D12_CULL_MODE_NONE;
        d.RasterizerState.DepthClipEnable = TRUE;
        d.PrimitiveTopologyType = D3D12_PRIMITIVE_TOPOLOGY_TYPE_TRIANGLE;
        d.NumRenderTargets = 1;
        d.RTVFormats[0] = format;
        d.SampleDesc.Count = 1;
        if (FAILED(dev->CreateGraphicsPipelineState(&d, IID_PPV_ARGS(&g_pso)))) return false;
        g_psoFormat = format;
        return true;
    }

    // Texture della misura dell'overlay. Restituisce falso se non c'è una texture da usare.
    // Se la misura è cambiata ma non si può ancora sostituire senza aspettare la scheda video (l'altro
    // posto SRV è ancora in uso, o troppi oggetti in attesa di essere liberati), si tiene la vecchia:
    // per un frame o due si vede l'immagine precedente invece di fermare il gioco.
    bool EnsureTexture(ID3D12Device* dev, const ff::Overlay& ov)
    {
        if (g_tex && g_texW == ov.width && g_texH == ov.height) return true;
        Purge(false);
        int other = 1 - g_srvSlot;
        UINT64 done = g_fence->GetCompletedValue();
        if (g_tex && (g_slotFence[other] > done || FreeDeadSlots() < 1 + kUploads)) return true;
        ReleaseTexture();

        D3D12_RESOURCE_DESC td = {};
        td.Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D;
        td.Width = ov.width;
        td.Height = ov.height;
        td.DepthOrArraySize = 1;
        td.MipLevels = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        D3D12_HEAP_PROPERTIES heap = { D3D12_HEAP_TYPE_DEFAULT };
        if (FAILED(dev->CreateCommittedResource(&heap, D3D12_HEAP_FLAG_NONE, &td,
            D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, nullptr, IID_PPV_ARGS(&g_tex))))
            return false;

        UINT64 total = 0;
        dev->GetCopyableFootprints(&td, 0, 1, 0, &g_footprint, nullptr, nullptr, &total);
        D3D12_RESOURCE_DESC bd = {};
        bd.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
        bd.Width = total;
        bd.Height = 1;
        bd.DepthOrArraySize = 1;
        bd.MipLevels = 1;
        bd.SampleDesc.Count = 1;
        bd.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
        D3D12_HEAP_PROPERTIES up = { D3D12_HEAP_TYPE_UPLOAD };
        for (int i = 0; i < kUploads; i++)
            if (FAILED(dev->CreateCommittedResource(&up, D3D12_HEAP_FLAG_NONE, &bd,
                D3D12_RESOURCE_STATE_GENERIC_READ, nullptr, IID_PPV_ARGS(&g_upload[i]))))
            {
                ReleaseTexture();
                return false;
            }

        g_srvSlot = other;
        D3D12_CPU_DESCRIPTOR_HANDLE srv = g_srvHeap->GetCPUDescriptorHandleForHeapStart();
        srv.ptr += (SIZE_T)g_srvSlot * g_srvStep;
        dev->CreateShaderResourceView(g_tex, nullptr, srv);
        g_texW = ov.width;
        g_texH = ov.height;
        g_texFresh = true;
        return true;
    }

    D3D12_RESOURCE_BARRIER Transition(ID3D12Resource* res, D3D12_RESOURCE_STATES from, D3D12_RESOURCE_STATES to)
    {
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = res;
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = from;
        b.Transition.StateAfter = to;
        return b;
    }
}

void ff::DrawD3D12(IDXGISwapChain* sc)
{
    Overlay ov;
    if (!GetOverlay(ov)) return;

    ID3D12Device* dev = nullptr;
    if (FAILED(sc->GetDevice(IID_PPV_ARGS(&dev))) || !dev) return;   // non è uno swapchain DirectX 12

    // La coda su cui presentare: quella dello swapchain se DXGI la rivela, altrimenti la si riconosce
    // tra quelle viste lavorare (mai una qualunque: vedi FindQueue).
    ID3D12CommandQueue* queue = nullptr;
    if (FAILED(sc->GetDevice(IID_PPV_ARGS(&queue))) || !queue)
    {
        queue = FindQueue(sc, dev);
        if (queue) queue->AddRef();
    }
    IDXGISwapChain3* sc3 = nullptr;
    ID3D12Resource* back = nullptr;

    if (queue && SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&sc3))) && sc3)
    {
        if (dev != g_dev || queue != g_queue)
        {
            ReleaseAll();
            g_dev = dev; dev->AddRef();
            g_queue = queue; queue->AddRef();
            g_failed = !CreateObjects(dev);
            Log("DirectX 12: risorse dell'overlay %s", g_failed ? "NON create" : "create");
        }

        UINT index = sc3->GetCurrentBackBufferIndex();
        if (!g_failed && index < kMaxFrames && SUCCEEDED(sc->GetBuffer(index, IID_PPV_ARGS(&back))) && back)
        {
            D3D12_RESOURCE_DESC bd = back->GetDesc();
            if (EnsurePipeline(dev, bd.Format) && EnsureTexture(dev, ov))
            {
                // Mai aspettare la scheda video dentro il Present del gioco: si prende un allocatore i cui
                // comandi sono già stati eseguiti. Se sono tutti ancora in coda (scheda video molto
                // indietro), per questo fotogramma si salta l'overlay invece di rallentare il gioco.
                Purge(false);
                UINT64 doneValue = g_fence->GetCompletedValue();
                int slot = -1;
                for (int i = 0; i < kMaxFrames && slot < 0; i++)
                    if (g_frameFence[i] <= doneValue) slot = i;
                ID3D12CommandAllocator* alloc = slot >= 0 ? g_alloc[slot] : nullptr;
                if (alloc && SUCCEEDED(alloc->Reset()) && SUCCEEDED(g_list->Reset(alloc, g_pso)))
                {
                    // Carica nella texture le righe cambiate (di solito solo la striscia del grafico), se la
                    // texture ha la misura dell'immagine e c'è un buffer di appoggio libero.
                    int upload = -1;
                    if ((g_texFresh || g_texSerial != ov.serial) && g_texW == ov.width && g_texH == ov.height)
                        for (int i = 0; i < kUploads && upload < 0; i++)
                            if (g_uploadFence[i] <= doneValue) upload = i;
                    void* mapped = nullptr;
                    D3D12_RANGE noRead = { 0, 0 };
                    if (upload >= 0 && SUCCEEDED(g_upload[upload]->Map(0, &noRead, &mapped)))
                    {
                        uint32_t y0, y1;
                        RowsToUpload(ov, g_texSerial, g_texFresh, y0, y1);
                        size_t row = (size_t)ov.width * 4;
                        UINT pitch = g_footprint.Footprint.RowPitch;
                        const uint8_t* src = ov.pixels + y0 * row;
                        uint8_t* dst = (uint8_t*)mapped + g_footprint.Offset + (size_t)y0 * pitch;
                        for (uint32_t y = y0; y < y1; y++, src += row, dst += pitch)
                            memcpy(dst, src, row);
                        g_upload[upload]->Unmap(0, nullptr);

                        auto toCopy = Transition(g_tex, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE, D3D12_RESOURCE_STATE_COPY_DEST);
                        g_list->ResourceBarrier(1, &toCopy);
                        D3D12_TEXTURE_COPY_LOCATION dstLoc = {}, srcLoc = {};
                        dstLoc.pResource = g_tex;
                        dstLoc.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX;
                        srcLoc.pResource = g_upload[upload];
                        srcLoc.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT;
                        srcLoc.PlacedFootprint = g_footprint;
                        D3D12_BOX box = { 0, y0, 0, ov.width, y1, 1 };
                        g_list->CopyTextureRegion(&dstLoc, 0, y0, 0, &srcLoc, &box);
                        auto toRead = Transition(g_tex, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE);
                        g_list->ResourceBarrier(1, &toRead);
                    }
                    else
                    {
                        upload = -1;
                    }

                    // Se la texture è ancora quella della misura precedente, la si disegna con la sua misura.
                    ff::Overlay shown = ov;
                    shown.width = g_texW;
                    shown.height = g_texH;

                    D3D12_CPU_DESCRIPTOR_HANDLE rtv = g_rtvHeap->GetCPUDescriptorHandleForHeapStart();
                    rtv.ptr += (SIZE_T)index * g_rtvStep;
                    dev->CreateRenderTargetView(back, nullptr, rtv);

                    int x, y;
                    Place(shown, (uint32_t)bd.Width, bd.Height, x, y);
                    D3D12_VIEWPORT vp = { (FLOAT)x, (FLOAT)y, (FLOAT)shown.width, (FLOAT)shown.height, 0.0f, 1.0f };
                    D3D12_RECT scissor = { 0, 0, (LONG)bd.Width, (LONG)bd.Height };

                    auto toTarget = Transition(back, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET);
                    g_list->ResourceBarrier(1, &toTarget);
                    g_list->OMSetRenderTargets(1, &rtv, FALSE, nullptr);
                    g_list->RSSetViewports(1, &vp);
                    g_list->RSSetScissorRects(1, &scissor);
                    g_list->SetGraphicsRootSignature(g_root);
                    g_list->SetDescriptorHeaps(1, &g_srvHeap);
                    D3D12_GPU_DESCRIPTOR_HANDLE srv = g_srvHeap->GetGPUDescriptorHandleForHeapStart();
                    srv.ptr += (UINT64)g_srvSlot * g_srvStep;
                    g_list->SetGraphicsRootDescriptorTable(0, srv);
                    // Colori adatti al back buffer (SDR, scRGB, HDR10): deciso solo quando cambia il formato.
                    ff::ColorConsts cc = ff::MakeColorConsts(ff::DxgiColorMode(sc, bd.Format), ov.whiteNits);
                    g_list->SetGraphicsRoot32BitConstants(1, sizeof(cc) / 4, &cc, 0);
                    g_list->IASetPrimitiveTopology(D3D_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
                    g_list->DrawInstanced(4, 1, 0, 0);
                    auto toPresent = Transition(back, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT);
                    g_list->ResourceBarrier(1, &toPresent);

                    if (SUCCEEDED(g_list->Close()))
                    {
                        ID3D12CommandList* lists[] = { g_list };
                        queue->ExecuteCommandLists(1, lists);
                        queue->Signal(g_fence, ++g_fenceValue);
                        g_frameFence[slot] = g_fenceValue;
                        g_slotFence[g_srvSlot] = g_fenceValue;
                        if (upload >= 0)
                        {
                            g_uploadFence[upload] = g_fenceValue;
                            g_texSerial = ov.serial;
                            g_texFresh = false;
                        }
                        MarkDrawn();
                    }
                }
            }
        }
    }

    SafeRelease(back);   // nessun riferimento ai buffer deve restare: il gioco deve poterli ridimensionare
    SafeRelease(sc3);
    SafeRelease(queue);
    dev->Release();
}

// Aggancia ExecuteCommandLists per scoprire la coda di comandi del gioco. Tutte le code condividono
// la stessa tabella di funzioni: ne creiamo una di prova per trovarla.
bool ff::HookD3D12()
{
    ID3D12Device* dev = nullptr;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev))) || !dev) return false;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ID3D12CommandQueue* queue = nullptr;
    bool ok = false;
    if (SUCCEEDED(dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue))) && queue)
    {
        void** vtable = *(void***)queue;
        ok = PatchVtable(vtable, kExecuteCommandLists, (void*)HookExecute, (void**)&g_origExecute);
        queue->Release();
    }
    dev->Release();
    return ok;
}
