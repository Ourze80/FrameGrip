// ff_testgame12: finto gioco DirectX 12 per provare l'overlay nei giochi moderni.
// Uso: ff_testgame12 [secondi] [activate] [fg [pipe] [hitch]] [off] [resize] [hdr16 | hdr10] [dump]
//   hdr16: swapchain scRGB (16 bit float);  hdr10: swapchain HDR10 (10 bit, PQ, BT.2020)
//   dump: fondo nero e, verso la fine, il fotogramma presentato in %TEMP%\ff_dump_d3d12.bin (vedi testdump.h)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d12.h>
#include <dxgi1_4.h>
#include "testdump.h"
#include <cmath>
#include <cstdlib>
#include <cwchar>

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int)
{
    double seconds = (cmd && *cmd) ? _wtof(cmd) : 30.0;
    const UINT kFrames = 2;
    // "unhook": la DLL si carica dopo, quando il gioco ha gia' messo da parte i puntatori della tabella (vedi sotto).
    bool unhookTest = cmd && wcsstr(cmd, L"unhook");
    if (!unhookTest) LoadTestHook();

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FrameGripTestGame12";
    RegisterClassExW(&wc);
    // "off": finestra fuori dallo schermo, visibile ma senza fuoco (le prove non disturbano chi usa il PC
    // e il gioco non viene frenato come quando è ridotto a icona).
    bool offscreen = TestOffscreen(cmd);
    // "resize": a 6 e 9 secondi cambia la misura della finestra e dei buffer dello swapchain.
    bool resizeTest = cmd && wcsstr(cmd, L"resize");
    HWND hwnd = CreateWindowExW(offscreen ? WS_EX_TOOLWINDOW : 0, wc.lpszClassName, L"FrameGrip Test Game (DirectX 12)", WS_OVERLAPPEDWINDOW,
        offscreen ? -3000 : 240, offscreen ? 100 : 240, offscreen ? 700 : 640, offscreen ? 800 : 360, nullptr, nullptr, inst, nullptr);
    bool activate = cmd && wcsstr(cmd, L"activate");
    bool fgMode = cmd && wcsstr(cmd, L"fg");
    // "hitch": a 10 secondi una pausa sola di 300 ms (uno scatto del gioco: caricamento, shader).
    // "pipe": gioco "a catena" come i motori veri: il lavoro per la coppia dopo va avanti mentre il Present
    // è fermo nel limitatore, quindi tra una coppia e l'altra restano solo 2 ms.
    bool hitchTest = cmd && wcsstr(cmd, L"hitch"), pipeMode = cmd && wcsstr(cmd, L"pipe"), hitchDone = false;
    // "stutter": da 8 a 16 secondi il gioco "compila gli shader": coppie lente (30-60 ms) e ogni tanto uno
    // scatto (120-300 ms); poi torna veloce. Serve a vedere che dopo il limite tenga ancora.
    bool stutterTest = cmd && wcsstr(cmd, L"stutter");
    unsigned stutterRnd = 12345;
    // "shader=P,A,B": da 8 secondi in poi il gioco "compila gli shader" senza sosta: P fotogrammi su 100
    // costano da A a B ms in piu' (scatti continui e a caso, come nei minuti di compilazione di un gioco vero).
    // "work=N": lavoro di base del gioco per ogni fotogramma, in decimi di millisecondo (attesa precisa).
    int shP = 0, shA = 0, shB = 0, workTenths = 0;
    if (cmd) { if (const wchar_t* a = wcsstr(cmd, L"shader=")) swscanf_s(a + 7, L"%d,%d,%d", &shP, &shA, &shB); }
    if (cmd) { if (const wchar_t* a = wcsstr(cmd, L"work=")) swscanf_s(a + 5, L"%d", &workTenths); }
    if (shB < shA) shB = shA;
    double pairSum = 0, gapSum = 0, pairSum2 = 0, gapSum2 = 0;   // "2" = dopo lo scatto
    int pairs = 0, pairs2 = 0;
    long long lastPair = 0;
    ShowWindow(hwnd, activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE);
    if (activate) SetForegroundWindow(hwnd);

    ID3D12Device* dev = nullptr;
    if (FAILED(D3D12CreateDevice(nullptr, D3D_FEATURE_LEVEL_11_0, IID_PPV_ARGS(&dev)))) return 1;
    D3D12_COMMAND_QUEUE_DESC qd = {};
    qd.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    ID3D12CommandQueue* queue = nullptr;
    dev->CreateCommandQueue(&qd, IID_PPV_ARGS(&queue));

    IDXGIFactory4* factory = nullptr;
    CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    DXGI_SWAP_CHAIN_DESC1 sd = {};
    sd.BufferCount = kFrames;
    bool hdr16 = cmd && wcsstr(cmd, L"hdr16"), hdr10 = cmd && wcsstr(cmd, L"hdr10");
    bool dump = cmd && wcsstr(cmd, L"dump"), dumped = false;
    sd.Format = hdr16 ? DXGI_FORMAT_R16G16B16A16_FLOAT : hdr10 ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    sd.SampleDesc.Count = 1;
    IDXGISwapChain1* sc1 = nullptr;
    if (FAILED(factory->CreateSwapChainForHwnd(queue, hwnd, &sd, nullptr, nullptr, &sc1))) return 2;
    IDXGISwapChain3* sc = nullptr;
    sc1->QueryInterface(IID_PPV_ARGS(&sc));
    // "unhook": fa la parte di un altro programma (ReShade, un overlay, una mod) che si era agganciato alla
    // tabella dello swapchain prima di FrameGrip e che, staccandosi a partita in corso, ci rimette i puntatori
    // che aveva trovato: a 10 secondi l'aggancio di FrameGrip sparisce dalla tabella. FrameGrip deve
    // accorgersene e rimettersi, altrimenti limitatore e misura si fermano senza che nessuno lo sappia.
    void* savedPresent = nullptr, * savedPresent1 = nullptr;
    bool unhookDone = false;
    if (unhookTest)
    {
        void** vt = *(void***)sc;
        savedPresent = vt[8];      // IDXGISwapChain::Present
        savedPresent1 = vt[22];    // IDXGISwapChain1::Present1
        LoadTestHook();
    }
    if (hdr16 || hdr10)
    {
        // Come fa un gioco HDR (o ReShade/RenoDX): lo spazio colore si dichiara dopo la creazione.
        // Lo si ripete dopo qualche secondo, quando FrameGrip è già agganciato (vedi sotto).
        DXGI_COLOR_SPACE_TYPE space = hdr16 ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
        UINT support = 0;
        sc->CheckColorSpaceSupport(space, &support);
        HRESULT hr = sc->SetColorSpace1(space);
        wchar_t path[MAX_PATH];
        GetTempPathW(MAX_PATH, path);
        wcscat_s(path, L"ff_hdr_result.txt");
        FILE* f = nullptr;
        if (_wfopen_s(&f, path, L"w") == 0 && f) { fprintf(f, "format %d space %d support 0x%x SetColorSpace1 hr=0x%08lX\n", (int)sd.Format, (int)space, support, (unsigned long)hr); fclose(f); }
    }

    D3D12_DESCRIPTOR_HEAP_DESC hd = { D3D12_DESCRIPTOR_HEAP_TYPE_RTV, kFrames, D3D12_DESCRIPTOR_HEAP_FLAG_NONE, 0 };
    ID3D12DescriptorHeap* heap = nullptr;
    dev->CreateDescriptorHeap(&hd, IID_PPV_ARGS(&heap));
    UINT step = dev->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
    ID3D12Resource* buffers[kFrames] = {};
    D3D12_CPU_DESCRIPTOR_HANDLE rtvs[kFrames];
    for (UINT i = 0; i < kFrames; i++)
    {
        sc->GetBuffer(i, IID_PPV_ARGS(&buffers[i]));
        rtvs[i] = heap->GetCPUDescriptorHandleForHeapStart();
        rtvs[i].ptr += (SIZE_T)i * step;
        dev->CreateRenderTargetView(buffers[i], nullptr, rtvs[i]);
    }

    ID3D12CommandAllocator* alloc = nullptr;
    ID3D12GraphicsCommandList* list = nullptr;
    dev->CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&alloc));
    dev->CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, alloc, nullptr, IID_PPV_ARGS(&list));
    list->Close();
    ID3D12Fence* fence = nullptr;
    dev->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(&fence));
    HANDLE fenceEvent = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    UINT64 fenceValue = 0;

    ULONGLONG start = GetTickCount64();
    MSG msg = {};
    bool running = true;
    long long countFrames = 0;     // fotogrammi dopo gli 8 s di avvio (aggancio): misura degli FPS
    LARGE_INTEGER countStart = {}, countEnd = {};
    int resizes = 0;
    while (running)
    {
        while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT) running = false;
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        double t = (GetTickCount64() - start) / 1000.0;
        if (t > seconds) break;
        if (unhookTest && !unhookDone && t > 10.0)
        {
            unhookDone = true;
            void** vt = *(void***)sc;
            DWORD old;
            if (VirtualProtect(&vt[8], sizeof(void*) * 15, PAGE_READWRITE, &old))
            {
                vt[8] = savedPresent;
                vt[22] = savedPresent1;
                VirtualProtect(&vt[8], sizeof(void*) * 15, old, &old);
            }
        }
        if (t > 8.0)
        {
            if (countFrames == 0) QueryPerformanceCounter(&countStart);
            countFrames++;
            QueryPerformanceCounter(&countEnd);
        }

        if (resizeTest && resizes < 2 && t > 6.0 + 3.0 * resizes)
        {
            // Come un gioco vero: si aspetta la scheda video, si lasciano i buffer e si ridimensiona.
            queue->Signal(fence, ++fenceValue);
            if (fence->GetCompletedValue() < fenceValue)
            {
                fence->SetEventOnCompletion(fenceValue, fenceEvent);
                WaitForSingleObject(fenceEvent, 1000);
            }
            for (UINT k = 0; k < kFrames; k++) { buffers[k]->Release(); buffers[k] = nullptr; }
            UINT w = resizes == 0 ? 1024 : 800, h = resizes == 0 ? 640 : 450;
            SetWindowPos(hwnd, nullptr, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
            HRESULT hr = sc->ResizeBuffers(kFrames, w, h, DXGI_FORMAT_UNKNOWN, 0);
            wchar_t path[MAX_PATH];
            GetTempPathW(MAX_PATH, path);
            wcscat_s(path, L"ff_resize_result.txt");
            FILE* f = nullptr;
            if (_wfopen_s(&f, path, resizes == 0 ? L"w" : L"a") == 0 && f) { fprintf(f, "resize %u x %u hr=0x%08lX\n", w, h, (unsigned long)hr); fclose(f); }
            if (FAILED(hr)) return 3;
            for (UINT k = 0; k < kFrames; k++)
            {
                sc->GetBuffer(k, IID_PPV_ARGS(&buffers[k]));
                dev->CreateRenderTargetView(buffers[k], nullptr, rtvs[k]);
            }
            resizes++;
        }

        // Lo spazio colore ridetto a 4 s, quando FrameGrip è agganciato ("nocs": no, per provare la deduzione
        // che serve quando il gioco l'ha detto prima dell'aggancio).
        static bool spaceAgain = false;
        if ((hdr16 || hdr10) && !spaceAgain && t > 4.0 && !wcsstr(cmd, L"nocs"))
        {
            spaceAgain = true;
            sc->SetColorSpace1(hdr16 ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);
        }

        UINT i = sc->GetCurrentBackBufferIndex();
        alloc->Reset();
        list->Reset(alloc, nullptr);
        D3D12_RESOURCE_BARRIER b = {};
        b.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
        b.Transition.pResource = buffers[i];
        b.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
        list->ResourceBarrier(1, &b);
        float color[4] = { 0.35f, (float)(0.5 + 0.5 * sin(t)), (float)(0.5 + 0.5 * sin(t * 1.3)), 1.0f };
        if (dump) color[0] = color[1] = color[2] = 0.0f;   // fondo nero: ogni pixel dell'overlay ha un valore atteso esatto
        list->ClearRenderTargetView(rtvs[i], color, 0, nullptr);
        b.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
        b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
        list->ResourceBarrier(1, &b);
        list->Close();
        ID3D12CommandList* lists[] = { list };
        queue->ExecuteCommandLists(1, lists);
        LARGE_INTEGER q0, q1, qf, qm;
        QueryPerformanceFrequency(&qf);
        QueryPerformanceCounter(&q0);
        sc->Present(0, 0);
        QueryPerformanceCounter(&qm);
        if (fgMode)
        {
            // Finta frame generation: un secondo fotogramma consegnato subito dopo il primo,
            // poi il "lavoro" del gioco per la coppia successiva.
            sc->Present(0, 0);
            QueryPerformanceCounter(&q1);
            if (t > 6.0 && t < 10.0 && lastPair != 0)   // i primi secondi servono a FrameGrip per agganciarsi e riconoscere il ritmo
            {
                pairSum += (double)(q0.QuadPart - lastPair) * 1000.0 / qf.QuadPart;
                gapSum += (double)(q1.QuadPart - qm.QuadPart) * 1000.0 / qf.QuadPart;
                pairs++;
            }
            else if (t > 12.0 && lastPair != 0)
            {
                pairSum2 += (double)(q0.QuadPart - lastPair) * 1000.0 / qf.QuadPart;
                gapSum2 += (double)(q1.QuadPart - qm.QuadPart) * 1000.0 / qf.QuadPart;
                pairs2++;
            }
            lastPair = q0.QuadPart;
            if (pipeMode)
            {
                // La coppia dopo è pronta 12 ms dopo l'inizio di questa, ma non prima di 2 ms da adesso.
                long long ready = q0.QuadPart + qf.QuadPart * 12 / 1000, soon = q1.QuadPart + qf.QuadPart * 2 / 1000;
                if (ready < soon) ready = soon;
                LARGE_INTEGER qn;
                for (;;)
                {
                    QueryPerformanceCounter(&qn);
                    if (qn.QuadPart >= ready) break;
                    if (ready - qn.QuadPart > qf.QuadPart * 2 / 1000) Sleep(1); else YieldProcessor();
                }
            }
            else Sleep(12);
            if (hitchTest && !hitchDone && t > 10.0) { hitchDone = true; Sleep(300); }
            if (stutterTest && t > 8.0 && t < 16.0)
            {
                stutterRnd = stutterRnd * 1664525u + 1013904223u;
                unsigned r = (stutterRnd >> 16) & 0xFF;
                Sleep(r < 20 ? 120 + r * 9 : 30 + r % 30);
            }
        }
        if (workTenths > 0 || (shP > 0 && t > 8.0))
        {
            double extraMs = workTenths / 10.0;
            if (shP > 0 && t > 8.0)
            {
                stutterRnd = stutterRnd * 1664525u + 1013904223u;
                unsigned r = (stutterRnd >> 16) & 0x7FFF;
                if ((int)(r % 100) < shP) extraMs += shA + (double)((r / 100) % (unsigned)(shB - shA + 1));
            }
            LARGE_INTEGER qa, qb;
            QueryPerformanceCounter(&qa);
            long long until = qa.QuadPart + (long long)(extraMs * qf.QuadPart / 1000.0);
            for (;;)
            {
                QueryPerformanceCounter(&qb);
                if (qb.QuadPart >= until) break;
                if (until - qb.QuadPart > qf.QuadPart * 2 / 1000) Sleep(1); else YieldProcessor();
            }
        }

        // Attende la scheda video prima di riusare l'allocatore (semplice, come nei tutorial).
        queue->Signal(fence, ++fenceValue);
        if (fence->GetCompletedValue() < fenceValue)
        {
            fence->SetEventOnCompletion(fenceValue, fenceEvent);
            WaitForSingleObject(fenceEvent, 1000);
        }

        // "dump": il fotogramma appena presentato (con l'overlay disegnato dalla DLL nel Present) torna
        // indietro dalla scheda video e finisce su disco insieme all'immagine dell'overlay dell'app.
        if (dump && !dumped && t > seconds - 1.5)
        {
            dumped = true;
            D3D12_RESOURCE_DESC rd = buffers[i]->GetDesc();
            D3D12_PLACED_SUBRESOURCE_FOOTPRINT fp;
            UINT64 total = 0;
            dev->GetCopyableFootprints(&rd, 0, 1, 0, &fp, nullptr, nullptr, &total);
            D3D12_HEAP_PROPERTIES hp = { D3D12_HEAP_TYPE_READBACK };
            D3D12_RESOURCE_DESC bdsc = {};
            bdsc.Dimension = D3D12_RESOURCE_DIMENSION_BUFFER;
            bdsc.Width = total;
            bdsc.Height = bdsc.DepthOrArraySize = bdsc.MipLevels = 1;
            bdsc.SampleDesc.Count = 1;
            bdsc.Layout = D3D12_TEXTURE_LAYOUT_ROW_MAJOR;
            ID3D12Resource* rb = nullptr;
            if (SUCCEEDED(dev->CreateCommittedResource(&hp, D3D12_HEAP_FLAG_NONE, &bdsc, D3D12_RESOURCE_STATE_COPY_DEST, nullptr, IID_PPV_ARGS(&rb))))
            {
                alloc->Reset();
                list->Reset(alloc, nullptr);
                b.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
                b.Transition.StateAfter = D3D12_RESOURCE_STATE_COPY_SOURCE;
                list->ResourceBarrier(1, &b);
                D3D12_TEXTURE_COPY_LOCATION dst = {}, src = {};
                dst.pResource = rb; dst.Type = D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT; dst.PlacedFootprint = fp;
                src.pResource = buffers[i]; src.Type = D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX; src.SubresourceIndex = 0;
                list->CopyTextureRegion(&dst, 0, 0, 0, &src, nullptr);
                b.Transition.StateBefore = D3D12_RESOURCE_STATE_COPY_SOURCE;
                b.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
                list->ResourceBarrier(1, &b);
                list->Close();
                queue->ExecuteCommandLists(1, lists);
                queue->Signal(fence, ++fenceValue);
                fence->SetEventOnCompletion(fenceValue, fenceEvent);
                WaitForSingleObject(fenceEvent, 2000);
                void* mapped = nullptr;
                if (SUCCEEDED(rb->Map(0, nullptr, &mapped)))
                {
                    WriteTestDump(L"d3d12", (uint8_t*)mapped + fp.Offset, (uint32_t)rd.Width, rd.Height, (uint32_t)rd.Format,
                                  hdr16 ? 8 : 4, fp.Footprint.RowPitch);
                    rb->Unmap(0, nullptr);
                }
                rb->Release();
            }
        }
    }
    if (countFrames > 1)
    {
        // FPS medi dopo l'avvio, in %TEMP%\ff_fps_<pid>.txt (per i confronti con e senza overlay).
        LARGE_INTEGER qf;
        QueryPerformanceFrequency(&qf);
        double secs = (double)(countEnd.QuadPart - countStart.QuadPart) / qf.QuadPart;
        wchar_t path[MAX_PATH], name[64];
        GetTempPathW(MAX_PATH, path);
        swprintf_s(name, L"ff_fps_%lu.txt", GetCurrentProcessId());
        wcscat_s(path, name);
        FILE* f = nullptr;
        if (secs > 0 && _wfopen_s(&f, path, L"w") == 0 && f)
        {
            fprintf(f, "%.2f\n", (countFrames - 1) / secs);
            fclose(f);
        }
    }
    if (fgMode && pairs > 0)
    {
        wchar_t path[MAX_PATH];
        GetTempPathW(MAX_PATH, path);
        wcscat_s(path, L"ff_fg_result.txt");
        FILE* f = nullptr;
        if (_wfopen_s(&f, path, L"w") == 0 && f)
        {
            fprintf(f, "pairs %d  period %.2f ms  second-present gap %.2f ms\n", pairs, pairSum / pairs, gapSum / pairs);
            if (pairs2 > 0)
                fprintf(f, "after  pairs %d  period %.2f ms  second-present gap %.2f ms\n", pairs2, pairSum2 / pairs2, gapSum2 / pairs2);
            fclose(f);
        }
    }
    return 0;
}
