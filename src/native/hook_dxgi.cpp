// Aggancio a DXGI (DirectX 10/11/12) e disegno dell'overlay con Direct3D 11.
// Con DirectX 12 il limitatore e la misura funzionano; il disegno nel gioco non ancora.
#include "common.h"
#include <d3d11.h>
#include <dxgi1_2.h>
#include <d3dcompiler.h>
#include <dxgi1_6.h>
#include <cstring>

// Shader dell'overlay per Direct3D 11 e 12. Il vertex shader fa un rettangolo che riempie il viewport,
// generato dal numero del vertice (nessun buffer). Il pixel shader adatta i colori al back buffer:
//  - SDR: copia l'immagine (sRGB, alfa premoltiplicato) così com'è, come sempre;
//  - altrimenti decodifica sRGB -> lineare. L'immagine è premoltiplicata nello spazio sRGB, quindi si
//    decodifica il colore premoltiplicato: su fondo nero dà esattamente quello che si vedrebbe in SDR.
//    Nei back buffer lineari (vista sRGB, scRGB) l'opacità usata per scurire il gioco diventa
//    1 - (1 - a)^2.2: così un fondo semitrasparente scurisce quanto in SDR (che mescola in gamma);
//  - scRGB: il bianco vale whiteNits / 80 (1.0 = 80 nit);
//  - HDR10: primari BT.709 -> BT.2020, poi curva PQ con il bianco a whiteNits. La miscela avviene nello
//    spazio PQ (non c'è un passaggio lineare): è percettivo come il gamma SDR, quindi i bordi del testo e
//    i fondi semitrasparenti restano simili a SDR, ma non identici sopra zone molto chiare del gioco.
namespace ff
{
    extern const char kOverlayHlsl[] =
        "struct VO { float4 pos : SV_Position; float2 uv : TEXCOORD0; };\n"
        "VO vs(uint id : SV_VertexID) {\n"
        "  VO o; float2 t = float2(id & 1, id >> 1);\n"
        "  o.uv = t; o.pos = float4(t.x * 2 - 1, 1 - t.y * 2, 0, 1); return o; }\n"
        "Texture2D tex : register(t0); SamplerState smp : register(s0);\n"
        "cbuffer Color : register(b0) { float mode; float scale; float2 pad; };\n"
        "float3 ToLinear(float3 c) { return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4); }\n"
        "float3 Pq(float3 nits) {\n"
        "  float3 y = pow(saturate(nits / 10000.0), 0.1593017578125);\n"
        "  return pow((0.8359375 + 18.8515625 * y) / (1.0 + 18.6875 * y), 78.84375); }\n"
        "float4 ps(VO i) : SV_Target {\n"
        "  float4 c = tex.Sample(smp, i.uv);\n"
        "  if (mode < 0.5) return c;\n"
        "  float3 lin = ToLinear(saturate(c.rgb));\n"
        "  float a = 1.0 - pow(saturate(1.0 - c.a), 2.2);\n"
        "  if (mode < 1.5) return float4(lin, a);\n"
        "  if (mode < 2.5) return float4(lin * scale, a);\n"
        "  float3 r = float3(dot(float3(0.627404, 0.329283, 0.043313), lin),\n"
        "                    dot(float3(0.069097, 0.919540, 0.011362), lin),\n"
        "                    dot(float3(0.016391, 0.088013, 0.895595), lin));\n"
        "  return float4(Pq(r * scale), c.a); }\n";
    extern const unsigned kOverlayHlslSize = sizeof(kOverlayHlsl) - 1;
}

namespace
{
    typedef HRESULT(STDMETHODCALLTYPE* PresentFn)(IDXGISwapChain*, UINT, UINT);
    typedef HRESULT(STDMETHODCALLTYPE* Present1Fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);

    const int kPresentIndex = 8;    // IDXGISwapChain::Present
    const int kPresent1Index = 22;  // IDXGISwapChain1::Present1

    PresentFn g_origPresent = nullptr;
    Present1Fn g_origPresent1 = nullptr;

    // Secondo livello ("N" = nativo). ReShade e programmi simili avvolgono ogni swapchain in un oggetto loro:
    // lo swapchain di prova torna avvolto e la tabella che si aggancia e' la loro, non quella di Windows.
    // Da li' si vedono solo i Present che passano dal loro oggetto: se la frame generation, una mod o il gioco
    // presentano direttamente sullo swapchain vero, limitatore e misura restano fuori senza saperlo (l'app
    // mostra allora gli FPS contati da Windows nella finestra esterna: contatore che gira, limite che non c'e').
    // Per questo, sotto un involucro, si aggancia anche la tabella vera di Windows, da cui passano tutti i
    // fotogrammi: quando lavora, guida lei e il livello di sopra lascia passare.
    PresentFn g_origPresentN = nullptr;
    Present1Fn g_origPresent1N = nullptr;
    volatile LONG64 g_nativeLastMs = 0;    // GetTickCount64 dell'ultimo Present visto al livello di Windows
    volatile LONG64 g_topLastMs = 0;       // lo stesso al livello di sopra
    volatile LONG g_drivingLevel = -1;     // 0 = tabella dello swapchain di prova, 1 = tabella di Windows
    volatile LONG g_levelLogs = 0;
    thread_local int t_topHandled = 0;     // il livello di sopra ha gia' contato questo fotogramma (stesso thread)

    // Gli agganci nelle tabelle, per controllarli una volta al secondo (DxgiMaintain).
    struct Slot { void** addr; void* hook; void** original; const char* name; void* reported; };
    Slot g_slots[6] = {};
    int g_slotCount = 0;
    volatile LONG g_slotLogs = 0;

    bool PatchSlot(void** vtable, int index, void* hook, void** original, const char* name)
    {
        if (!ff::PatchVtable(vtable, index, hook, original)) return false;
        if (g_slotCount < 6) g_slots[g_slotCount++] = { &vtable[index], hook, original, name, nullptr };
        return true;
    }

    // ---------------------------------------------------------------- disegno D3D11

    ID3D11Device* g_dev = nullptr;   // solo per confronto: le risorse qui sotto tengono vivo il device
    ID3D11Texture2D* g_tex = nullptr;
    ID3D11ShaderResourceView* g_srv = nullptr;
    ID3D11VertexShader* g_vs = nullptr;
    ID3D11PixelShader* g_ps = nullptr;
    ID3D11BlendState* g_blend = nullptr;
    ID3D11RasterizerState* g_raster = nullptr;
    ID3D11DepthStencilState* g_depth = nullptr;
    ID3D11SamplerState* g_sampler = nullptr;
    ID3D11Buffer* g_cb = nullptr;      // costanti del colore (ColorConsts): riscritte solo quando cambiano
    ff::ColorConsts g_consts = {};
    uint32_t g_texW = 0, g_texH = 0, g_texSerial = 0;
    bool g_texFresh = true;   // texture appena creata: va caricata tutta
    bool g_failed = false;

    template <typename T> void SafeRelease(T*& p)
    {
        if (p) { p->Release(); p = nullptr; }
    }

    void ReleaseAll()
    {
        SafeRelease(g_srv);
        SafeRelease(g_tex);
        SafeRelease(g_vs);
        SafeRelease(g_ps);
        SafeRelease(g_blend);
        SafeRelease(g_raster);
        SafeRelease(g_depth);
        SafeRelease(g_sampler);
        SafeRelease(g_cb);
        g_texW = g_texH = 0;
        g_dev = nullptr;
    }

    const char* kShader = ff::kOverlayHlsl;

    bool CreateObjects(ID3D11Device* dev)
    {
        ID3DBlob* vsBlob = nullptr;
        ID3DBlob* psBlob = nullptr;
        bool ok = false;
        if (SUCCEEDED(D3DCompile(kShader, ff::kOverlayHlslSize, nullptr, nullptr, nullptr, "vs", "vs_4_0", 0, 0, &vsBlob, nullptr)) &&
            SUCCEEDED(D3DCompile(kShader, ff::kOverlayHlslSize, nullptr, nullptr, nullptr, "ps", "ps_4_0", 0, 0, &psBlob, nullptr)) &&
            SUCCEEDED(dev->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &g_vs)) &&
            SUCCEEDED(dev->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &g_ps)))
        {
            D3D11_BLEND_DESC bd = {};
            bd.RenderTarget[0].BlendEnable = TRUE;
            bd.RenderTarget[0].SrcBlend = D3D11_BLEND_ONE;              // alfa premoltiplicato
            bd.RenderTarget[0].DestBlend = D3D11_BLEND_INV_SRC_ALPHA;
            bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
            bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
            bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_INV_SRC_ALPHA;
            bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
            bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;

            D3D11_RASTERIZER_DESC rd = {};
            rd.FillMode = D3D11_FILL_SOLID;
            rd.CullMode = D3D11_CULL_NONE;
            rd.DepthClipEnable = TRUE;

            D3D11_DEPTH_STENCIL_DESC dd = {};
            dd.DepthEnable = FALSE;
            dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
            dd.DepthFunc = D3D11_COMPARISON_ALWAYS;

            D3D11_SAMPLER_DESC sd = {};
            sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
            sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
            sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
            sd.MaxLOD = D3D11_FLOAT32_MAX;

            D3D11_BUFFER_DESC cd = {};
            cd.ByteWidth = sizeof(ff::ColorConsts);
            cd.Usage = D3D11_USAGE_DEFAULT;
            cd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
            ff::ColorConsts sdr = ff::MakeColorConsts(ff::kColorSdr, 200.0f);
            D3D11_SUBRESOURCE_DATA init = { &sdr, 0, 0 };
            g_consts = sdr;

            ok = SUCCEEDED(dev->CreateBuffer(&cd, &init, &g_cb)) &&
                 SUCCEEDED(dev->CreateBlendState(&bd, &g_blend)) &&
                 SUCCEEDED(dev->CreateRasterizerState(&rd, &g_raster)) &&
                 SUCCEEDED(dev->CreateDepthStencilState(&dd, &g_depth)) &&
                 SUCCEEDED(dev->CreateSamplerState(&sd, &g_sampler));
        }
        if (vsBlob) vsBlob->Release();
        if (psBlob) psBlob->Release();
        return ok;
    }

    bool UploadTexture(ID3D11Device* dev, ID3D11DeviceContext* ctx, const ff::Overlay& ov)
    {
        if (!g_tex || g_texW != ov.width || g_texH != ov.height)
        {
            SafeRelease(g_srv);
            SafeRelease(g_tex);
            D3D11_TEXTURE2D_DESC td = {};
            td.Width = ov.width;
            td.Height = ov.height;
            td.MipLevels = 1;
            td.ArraySize = 1;
            td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            td.SampleDesc.Count = 1;
            // Texture normale aggiornata con UpdateSubresource: si può ricaricare solo la striscia cambiata
            // (con WRITE_DISCARD andrebbe riscritta tutta). Il driver copia i dati senza far aspettare il gioco.
            td.Usage = D3D11_USAGE_DEFAULT;
            td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            if (FAILED(dev->CreateTexture2D(&td, nullptr, &g_tex))) return false;
            if (FAILED(dev->CreateShaderResourceView(g_tex, nullptr, &g_srv))) return false;
            g_texW = ov.width;
            g_texH = ov.height;
            g_texFresh = true;
        }
        if (g_texFresh || g_texSerial != ov.serial)
        {
            uint32_t y0, y1;
            ff::RowsToUpload(ov, g_texSerial, g_texFresh, y0, y1);
            size_t row = (size_t)ov.width * 4;
            D3D11_BOX box = { 0, y0, 0, ov.width, y1, 1 };
            ctx->UpdateSubresource(g_tex, 0, &box, ov.pixels + y0 * row, (UINT)row, 0);
            g_texSerial = ov.serial;
            g_texFresh = false;
        }
        return true;
    }

    // Stato del gioco da rimettere a posto dopo il nostro disegno.
    struct Backup
    {
        UINT scissorCount, viewportCount;
        D3D11_RECT scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
        D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
        ID3D11RasterizerState* raster;
        ID3D11BlendState* blend;
        FLOAT blendFactor[4];
        UINT sampleMask, stencilRef;
        ID3D11DepthStencilState* depth;
        ID3D11ShaderResourceView* psSrv;
        ID3D11SamplerState* psSampler;
        ID3D11Buffer* psCb;
        ID3D11PixelShader* ps;
        ID3D11VertexShader* vs;
        ID3D11GeometryShader* gs;
        ID3D11HullShader* hs;
        ID3D11DomainShader* ds;
        UINT psInstCount, vsInstCount, gsInstCount;
        ID3D11ClassInstance* psInst[256];
        ID3D11ClassInstance* vsInst[256];
        ID3D11ClassInstance* gsInst[256];
        D3D11_PRIMITIVE_TOPOLOGY topology;
        ID3D11InputLayout* layout;
        ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT];
        ID3D11DepthStencilView* dsv;
    };

    void Save(ID3D11DeviceContext* ctx, Backup& b)
    {
        b.scissorCount = b.viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
        ctx->RSGetScissorRects(&b.scissorCount, b.scissors);
        ctx->RSGetViewports(&b.viewportCount, b.viewports);
        ctx->RSGetState(&b.raster);
        ctx->OMGetBlendState(&b.blend, b.blendFactor, &b.sampleMask);
        ctx->OMGetDepthStencilState(&b.depth, &b.stencilRef);
        ctx->PSGetShaderResources(0, 1, &b.psSrv);
        ctx->PSGetSamplers(0, 1, &b.psSampler);
        ctx->PSGetConstantBuffers(0, 1, &b.psCb);
        b.psInstCount = b.vsInstCount = b.gsInstCount = 256;
        ctx->PSGetShader(&b.ps, b.psInst, &b.psInstCount);
        ctx->VSGetShader(&b.vs, b.vsInst, &b.vsInstCount);
        ctx->GSGetShader(&b.gs, b.gsInst, &b.gsInstCount);
        ctx->HSGetShader(&b.hs, nullptr, nullptr);
        ctx->DSGetShader(&b.ds, nullptr, nullptr);
        ctx->IAGetPrimitiveTopology(&b.topology);
        ctx->IAGetInputLayout(&b.layout);
        ctx->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, b.rtvs, &b.dsv);
    }

    void Restore(ID3D11DeviceContext* ctx, Backup& b)
    {
        ctx->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, b.rtvs, b.dsv);
        for (auto*& r : b.rtvs) SafeRelease(r);
        SafeRelease(b.dsv);
        ctx->RSSetScissorRects(b.scissorCount, b.scissors);
        ctx->RSSetViewports(b.viewportCount, b.viewports);
        ctx->RSSetState(b.raster); SafeRelease(b.raster);
        ctx->OMSetBlendState(b.blend, b.blendFactor, b.sampleMask); SafeRelease(b.blend);
        ctx->OMSetDepthStencilState(b.depth, b.stencilRef); SafeRelease(b.depth);
        ctx->PSSetShaderResources(0, 1, &b.psSrv); SafeRelease(b.psSrv);
        ctx->PSSetSamplers(0, 1, &b.psSampler); SafeRelease(b.psSampler);
        ctx->PSSetConstantBuffers(0, 1, &b.psCb); SafeRelease(b.psCb);
        ctx->PSSetShader(b.ps, b.psInst, b.psInstCount); SafeRelease(b.ps);
        for (UINT i = 0; i < b.psInstCount; i++) SafeRelease(b.psInst[i]);
        ctx->VSSetShader(b.vs, b.vsInst, b.vsInstCount); SafeRelease(b.vs);
        for (UINT i = 0; i < b.vsInstCount; i++) SafeRelease(b.vsInst[i]);
        ctx->GSSetShader(b.gs, b.gsInst, b.gsInstCount); SafeRelease(b.gs);
        for (UINT i = 0; i < b.gsInstCount; i++) SafeRelease(b.gsInst[i]);
        ctx->HSSetShader(b.hs, nullptr, 0); SafeRelease(b.hs);
        ctx->DSSetShader(b.ds, nullptr, 0); SafeRelease(b.ds);
        ctx->IASetPrimitiveTopology(b.topology);
        ctx->IASetInputLayout(b.layout); SafeRelease(b.layout);
    }

    void DrawD3D11(IDXGISwapChain* sc)
    {
        ff::Overlay ov;
        if (!ff::GetOverlay(ov)) return;

        ID3D11Device* dev = nullptr;
        if (FAILED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) || !dev)
        {
            ff::DrawD3D12(sc);   // non è Direct3D 11: si prova con DirectX 12
            return;
        }

        if (dev != g_dev)
        {
            ReleaseAll();
            g_dev = dev;
            g_failed = !CreateObjects(dev);
        }

        ID3D11DeviceContext* ctx = nullptr;
        ID3D11Texture2D* back = nullptr;
        ID3D11RenderTargetView* rtv = nullptr;
        if (!g_failed)
        {
            dev->GetImmediateContext(&ctx);
            if (ctx && SUCCEEDED(sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back)) && back &&
                SUCCEEDED(dev->CreateRenderTargetView(back, nullptr, &rtv)) && rtv &&
                UploadTexture(dev, ctx, ov))
            {
                D3D11_TEXTURE2D_DESC bd;
                back->GetDesc(&bd);
                int x, y;
                ff::Place(ov, bd.Width, bd.Height, x, y);

                static Backup backup;   // grande: fuori dallo stack del gioco
                Save(ctx, backup);

                // Colori adatti al back buffer (SDR, vista sRGB, scRGB, HDR10): costanti riscritte solo se cambiano.
                ff::ColorConsts cc = ff::MakeColorConsts(ff::DxgiColorMode(sc, bd.Format), ov.whiteNits);
                if (memcmp(&cc, &g_consts, sizeof(cc)) != 0)
                {
                    ctx->UpdateSubresource(g_cb, 0, nullptr, &cc, 0, 0);
                    g_consts = cc;
                }

                D3D11_VIEWPORT vp = { (FLOAT)x, (FLOAT)y, (FLOAT)ov.width, (FLOAT)ov.height, 0.0f, 1.0f };
                D3D11_RECT scissor = { 0, 0, (LONG)bd.Width, (LONG)bd.Height };
                const FLOAT blendFactor[4] = { 0, 0, 0, 0 };
                ctx->OMSetRenderTargets(1, &rtv, nullptr);
                ctx->RSSetViewports(1, &vp);
                ctx->RSSetScissorRects(1, &scissor);
                ctx->RSSetState(g_raster);
                ctx->OMSetBlendState(g_blend, blendFactor, 0xFFFFFFFF);
                ctx->OMSetDepthStencilState(g_depth, 0);
                ctx->IASetInputLayout(nullptr);
                ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
                ctx->VSSetShader(g_vs, nullptr, 0);
                ctx->PSSetShader(g_ps, nullptr, 0);
                ctx->GSSetShader(nullptr, nullptr, 0);
                ctx->HSSetShader(nullptr, nullptr, 0);
                ctx->DSSetShader(nullptr, nullptr, 0);
                ctx->PSSetShaderResources(0, 1, &g_srv);
                ctx->PSSetSamplers(0, 1, &g_sampler);
                ctx->PSSetConstantBuffers(0, 1, &g_cb);
                ctx->Draw(4, 0);

                Restore(ctx, backup);
                ff::MarkDrawn();
            }
        }

        SafeRelease(rtv);
        SafeRelease(back);
        SafeRelease(ctx);
        dev->Release();
    }

    // ---------------------------------------------------------------- spazio colore (HDR)

    // Ultimo SetColorSpace1 riuscito per ogni swapchain. Scritto solo dalla chiamata del gioco (rara),
    // letto nel Present senza blocchi: ogni voce è una coppia di valori allineati.
    struct SpaceEntry { IDXGISwapChain* volatile sc; volatile LONG space; };
    SpaceEntry g_spaces[16] = {};
    volatile LONG g_spaceNext = 0;

    typedef HRESULT(STDMETHODCALLTYPE* SetColorSpace1Fn)(IDXGISwapChain3*, DXGI_COLOR_SPACE_TYPE);
    const int kSetColorSpace1Index = 38;   // IDXGISwapChain3::SetColorSpace1
    SetColorSpace1Fn g_origSetColorSpace1 = nullptr, g_origSetColorSpace1N = nullptr;

    void RememberSpace(IDXGISwapChain* key, DXGI_COLOR_SPACE_TYPE space)
    {
        SpaceEntry* slot = nullptr;
        for (auto& e : g_spaces)
            if (e.sc == key) { slot = &e; break; }
        if (!slot) slot = &g_spaces[(InterlockedIncrement(&g_spaceNext) - 1) & 15];
        slot->space = (LONG)space;
        MemoryBarrier();
        slot->sc = key;
    }

    HRESULT STDMETHODCALLTYPE HookSetColorSpace1(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE space)
    {
        HRESULT hr = g_origSetColorSpace1(sc, space);
        if (SUCCEEDED(hr)) RememberSpace(sc, space);
        return hr;
    }

    // Lo stesso al livello di Windows (sotto un involucro): li' lo swapchain e' un altro oggetto.
    HRESULT STDMETHODCALLTYPE HookSetColorSpace1N(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE space)
    {
        HRESULT hr = g_origSetColorSpace1N(sc, space);
        if (SUCCEEDED(hr)) RememberSpace(sc, space);
        return hr;
    }

    // -1 = il gioco non l'ha detto (o l'ha detto prima che FrameGrip arrivasse).
    LONG KnownSpace(IDXGISwapChain* sc)
    {
        for (auto& e : g_spaces)
            if (e.sc == sc) { LONG s = e.space; MemoryBarrier(); return e.sc == sc ? s : -1; }
        return -1;
    }

    // Lo schermo su cui sta la finestra è in modalità HDR? Serve solo per i 10 bit quando lo spazio colore
    // non si conosce (gioco partito prima dell'aggancio). Si chiede una volta per swapchain e formato.
    bool OutputIsHdr(IDXGISwapChain* sc)
    {
        bool hdr = false;
        IDXGIOutput* out = nullptr;
        if (SUCCEEDED(sc->GetContainingOutput(&out)) && out)
        {
            IDXGIOutput6* out6 = nullptr;
            DXGI_OUTPUT_DESC1 d;
            if (SUCCEEDED(out->QueryInterface(__uuidof(IDXGIOutput6), (void**)&out6)) && out6 && SUCCEEDED(out6->GetDesc1(&d)))
                hdr = d.ColorSpace == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020;
            SafeRelease(out6);
            out->Release();
        }
        return hdr;
    }

    // ---------------------------------------------------------------- aggancio

    // Quale DirectX usa il gioco: lo dice il tipo di device a cui appartiene lo swapchain.
    // Si chiede una volta per swapchain (vedi SwapInfo).
    uint32_t ApiOf(IDXGISwapChain* sc)
    {
        static const GUID iidD3D10 = { 0x9B7E4C0F, 0x342C, 0x4106, { 0xA1, 0x9F, 0x4F, 0x27, 0x04, 0xF6, 0x89, 0xF0 } };   // ID3D10Device
        static const GUID iidD3D12 = { 0x189819F1, 0x1DB6, 0x4B57, { 0xBE, 0x54, 0x18, 0x21, 0x33, 0x9B, 0x85, 0xF7 } };   // ID3D12Device
        uint32_t api = FF_API_DXGI;
        IUnknown* dev = nullptr;
        if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) && dev) api = FF_API_D3D11;
        else if (SUCCEEDED(sc->GetDevice(iidD3D12, (void**)&dev)) && dev) api = FF_API_D3D12;
        else if (SUCCEEDED(sc->GetDevice(iidD3D10, (void**)&dev)) && dev) api = FF_API_D3D10;
        if (dev) dev->Release();
        return api;
    }

    // ---------------------------------------------------------------- swapchain del gioco
    // Pochi dati per ogni swapchain che presenta (di solito uno, a volte due: es. una finestra secondaria).
    // Con più swapchain solo il "principale" (quello che presenta più spesso) muove limitatore e misura
    // e riceve l'overlay; gli altri passano senza attese.
    struct SwapInfo
    {
        IDXGISwapChain* sc;       // solo per confronto
        uint32_t api;             // FF_API_*
        bool stillDrawing;        // l'ultimo Present ha risposto DXGI_ERROR_WAS_STILL_DRAWING (DO_NOT_WAIT)
        uint32_t count;           // Present nell'ultima finestra di misura
        uint64_t lastMs;          // ultimo Present (GetTickCount64)
        uint32_t format;          // DxgiColorMode: ultimo formato e spazio colore visti, e il modo deciso
        LONG space;
        uint32_t mode;
    };
    const int kSwapMax = 8;
    // Un elenco per livello (0 = tabella dello swapchain di prova, 1 = tabella di Windows sotto un involucro):
    // lo stesso fotogramma passa da tutti e due con due oggetti swapchain diversi, e ognuno dei due livelli
    // deve poter scegliere il suo "principale" senza contare quelli dell'altro.
    struct SwapSet { SwapInfo swaps[kSwapMax]; IDXGISwapChain* primary; uint64_t windowStartMs; };
    SwapSet g_swapSets[2] = {};
    SRWLOCK g_swapLock = SRWLOCK_INIT;   // tenuto per pochi istanti, mai durante chiamate a DXGI
    volatile LONG g_modeLogs = 0;         // righe "Overlay: colori" scritte (con un tetto)
    volatile LONG g_primaryLogs = 0;      // righe "DXGI: swapchain principale" scritte (con un tetto)

    SwapInfo* FindSwap(IDXGISwapChain* sc, int level)
    {
        for (auto& s : g_swapSets[level].swaps)
            if (s.sc == sc) return &s;
        return nullptr;
    }

    SwapInfo* FindSwap(IDXGISwapChain* sc)
    {
        SwapInfo* s = FindSwap(sc, 0);
        return s ? s : FindSwap(sc, 1);
    }

    // Prima del Present: "retry" = è il nuovo tentativo di un Present rifiutato (DO_NOT_WAIT), "primary" =
    // questo swapchain guida limitatore, misura e overlay.
    void BeginPresent(int level, IDXGISwapChain* sc, bool& retry, bool& primary, uint32_t& api)
    {
        auto& g_swaps = g_swapSets[level].swaps;
        IDXGISwapChain*& g_primary = g_swapSets[level].primary;
        uint64_t& g_windowStartMs = g_swapSets[level].windowStartMs;
        uint64_t now = GetTickCount64();
        AcquireSRWLockShared(&g_swapLock);
        bool known = FindSwap(sc, level) != nullptr;
        ReleaseSRWLockShared(&g_swapLock);
        uint32_t newApi = known ? 0 : ApiOf(sc);   // fuori dal blocco: chiama DXGI

        AcquireSRWLockExclusive(&g_swapLock);
        SwapInfo* s = FindSwap(sc, level);
        if (!s)
        {
            // Posto libero, altrimenti quello fermo da più tempo (uno swapchain buttato dal gioco).
            s = &g_swaps[0];
            for (auto& e : g_swaps)
            {
                if (!e.sc) { s = &e; break; }
                if (e.lastMs < s->lastMs) s = &e;
            }
            if (s->sc == g_primary) g_primary = nullptr;
            *s = {};
            s->sc = sc;
            s->api = newApi ? newApi : FF_API_DXGI;
            s->format = 0xFFFFFFFF;
            s->space = -2;
        }
        retry = s->stillDrawing;
        s->count++;
        s->lastMs = now;
        IDXGISwapChain* primaryBefore = g_primary;
        if (!g_primary) g_primary = sc;
        if (now - g_windowStartMs >= 500)
        {
            // Ogni mezzo secondo: principale = chi ha presentato di più (a pari merito resta quello di prima).
            SwapInfo* best = FindSwap(g_primary, level);
            for (auto& e : g_swaps)
                if (e.sc && (!best || e.count > best->count)) best = &e;
            if (best) g_primary = best->sc;
            for (auto& e : g_swaps) e.count = 0;
            g_windowStartMs = now;
        }
        primary = g_primary == sc;
        api = s->api;
        IDXGISwapChain* primaryNow = g_primary;
        int active = 0;
        for (auto& e : g_swaps) if (e.sc && now - e.lastMs < 2000) active++;
        ReleaseSRWLockExclusive(&g_swapLock);
        // Diario, fuori dal blocco e con un tetto: quando cambia lo swapchain che guida limitatore e overlay.
        if (primaryNow != primaryBefore && InterlockedIncrement(&g_primaryLogs) <= 24)
            ff::Log("DXGI: swapchain principale %p (prima %p), %d attivi%s", (void*)primaryNow, (void*)primaryBefore, active,
                    level == 1 ? " (tabella di Windows)" : "");
    }

    void EndPresent(int level, IDXGISwapChain* sc, HRESULT hr)
    {
        AcquireSRWLockExclusive(&g_swapLock);
        if (SwapInfo* s = FindSwap(sc, level)) s->stillDrawing = hr == DXGI_ERROR_WAS_STILL_DRAWING;
        ReleaseSRWLockExclusive(&g_swapLock);
    }

    // level: 0 = tabella trovata con lo swapchain di prova, 1 = tabella di Windows sotto un involucro.
    template <typename F> HRESULT PresentCommon(IDXGISwapChain* sc, UINT flags, int level, F callOriginal)
    {
        if (flags & DXGI_PRESENT_TEST) return callOriginal();
        // Con due livelli agganciati i compiti si dividono:
        //  - conta e frena ("frames") il livello di Windows, da cui passano tutti i fotogrammi; quello di sopra
        //    lo fa solo finche' da Windows non ne vede passare (mezzo secondo);
        //  - disegna l'overlay ("draw") il livello di sopra, come sempre: e' li' che si conosce la coda
        //    DirectX 12 dell'involucro. Quello di Windows disegna solo se di sopra non passa piu' niente.
        uint64_t now = GetTickCount64();
        bool frames = true, draw = true;
        if (level == 1)
        {
            InterlockedExchange64(&g_nativeLastMs, (LONG64)now);
            // Lo stesso fotogramma, gia' contato e atteso un attimo fa al livello di sopra (stesso thread).
            if (t_topHandled > 0) return callOriginal();
            draw = now - (uint64_t)g_topLastMs >= 500;
        }
        else
        {
            InterlockedExchange64(&g_topLastMs, (LONG64)now);
            frames = !(g_origPresentN && now - (uint64_t)g_nativeLastMs < 500);
        }
        if (frames && g_origPresentN && InterlockedExchange(&g_drivingLevel, level) != level && InterlockedIncrement(&g_levelLogs) <= 12)
            ff::Log(level == 1 ? "DXGI: i fotogrammi si contano alla tabella di Windows (sotto l'involucro)"
                               : "DXGI: i fotogrammi si contano alla tabella dell'involucro (da quella di Windows non ne passano)");
        bool retry = false, primary = true;
        uint32_t api = FF_API_DXGI;
        BeginPresent(level, sc, retry, primary, api);
        // Nuovo tentativo dopo DXGI_ERROR_WAS_STILL_DRAWING: l'overlay è già nel back buffer e il frame è
        // già stato contato e atteso, quindi qui non si fa nulla.
        if (!retry && primary)
        {
            if (draw && ff::OwnsFrames(api))
            {
                ff::OverlayTryLock overlayLock;   // due thread che presentano: uno solo disegna, mai attese
                if (overlayLock.locked) DrawD3D11(sc);
            }
            if (frames) ff::OnFrame(api);
        }
        else if (frames)
        {
            InterlockedIncrement(retry ? &ff::g_diagRetry : &ff::g_diagNotPrimary);   // solo per il diario
        }
        bool nested = level == 0 && frames;   // il livello di Windows, qui sotto, non deve contarlo un'altra volta
        if (nested) t_topHandled++;
        HRESULT hr = callOriginal();
        if (nested) t_topHandled--;
        EndPresent(level, sc, hr);
        return hr;
    }

    HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* sc, UINT sync, UINT flags)
    {
        return PresentCommon(sc, flags, 0, [&] { return g_origPresent(sc, sync, flags); });
    }

    HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
    {
        return PresentCommon(sc, flags, 0, [&] { return g_origPresent1(sc, sync, flags, params); });
    }

    HRESULT STDMETHODCALLTYPE HookPresentN(IDXGISwapChain* sc, UINT sync, UINT flags)
    {
        return PresentCommon(sc, flags, 1, [&] { return g_origPresentN(sc, sync, flags); });
    }

    HRESULT STDMETHODCALLTYPE HookPresent1N(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
    {
        return PresentCommon(sc, flags, 1, [&] { return g_origPresent1N(sc, sync, flags, params); });
    }

    // ---------------------------------------------------------------- lo swapchain vero sotto un involucro

    bool Readable(const void* p, size_t bytes)
    {
        MEMORY_BASIC_INFORMATION mbi;
        if (!p || !VirtualQuery(p, &mbi, sizeof(mbi))) return false;
        if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
        return (const uint8_t*)p + bytes <= (const uint8_t*)mbi.BaseAddress + mbi.RegionSize;
    }

    HMODULE ModuleOf(const void* p)
    {
        HMODULE m = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)p, &m);
        return m;
    }

    // dxgi.dll di Windows (in System32), non un "dxgi.dll" messo nella cartella del gioco.
    HMODULE SystemDxgi()
    {
        wchar_t path[MAX_PATH];
        UINT n = GetSystemDirectoryW(path, MAX_PATH);
        if (!n || n > MAX_PATH - 12) return nullptr;
        wcscat_s(path, L"\\dxgi.dll");
        return GetModuleHandleW(path);
    }

    // Un involucro tiene tra i suoi campi il puntatore allo swapchain vero: lo si riconosce perche' la sua
    // tabella sta dentro dxgi.dll di Windows e perche' risponde come swapchain. Si guardano solo i primi campi,
    // sempre controllando che la memoria si possa leggere. Restituisce lo swapchain con un riferimento in piu'.
    IDXGISwapChain* FindNative(IDXGISwapChain* wrapper, HMODULE systemDxgi)
    {
        void** fields = (void**)wrapper;
        for (int i = 1; i < 48; i++)
        {
            if (!Readable(&fields[i], sizeof(void*))) break;
            void* q = fields[i];
            if (!q || ((uintptr_t)q & (sizeof(void*) - 1)) || !Readable(q, sizeof(void*))) continue;
            void** vt = *(void***)q;
            if (!Readable(vt, sizeof(void*) * 3) || ModuleOf(vt) != systemDxgi) continue;
            IDXGISwapChain* real = nullptr;
            if (SUCCEEDED(((IUnknown*)q)->QueryInterface(__uuidof(IDXGISwapChain), (void**)&real)) && real)
            {
                if (ModuleOf(*(void***)real) == systemDxgi) return real;
                real->Release();
            }
        }
        return nullptr;
    }
}

// Una volta al secondo, dal thread dell'aggancio: gli agganci nelle tabelle sono ancora al loro posto?
// Un altro programma agganciato alla stessa tabella prima di noi, quando si stacca, ci rimette il puntatore
// che aveva trovato lui: il nostro sparisce e limitatore, misura e overlay si fermano in silenzio.
//  - se nella tabella e' tornato proprio il puntatore che avevamo trovato noi, ci si rimette (non cambia
//    niente per nessuno: la catena e' quella di prima);
//  - se c'e' il puntatore di qualcun altro lo si lascia (di solito e' chi si e' agganciato sopra di noi e
//    poi ci chiama): lo si scrive solo nel diario.
void ff::DxgiMaintain()
{
    for (int i = 0; i < g_slotCount; i++)
    {
        Slot& s = g_slots[i];
        void* cur = *s.addr;
        if (cur == s.hook) { s.reported = nullptr; continue; }
        if (cur == *s.original)
        {
            DWORD old;
            if (VirtualProtect(s.addr, sizeof(void*), PAGE_EXECUTE_READWRITE, &old))
            {
                *s.addr = s.hook;
                VirtualProtect(s.addr, sizeof(void*), old, &old);
            }
            if (InterlockedIncrement(&g_slotLogs) <= 24)
                Log("DXGI: %s: un altro programma aveva tolto l'aggancio, rimesso", s.name);
        }
        else if (s.reported != cur)
        {
            s.reported = cur;
            wchar_t path[MAX_PATH] = L"?";
            if (HMODULE m = ModuleOf(cur)) GetModuleFileNameW(m, path, MAX_PATH);
            if (InterlockedIncrement(&g_slotLogs) <= 24)
                Log("DXGI: %s: nella tabella ora c'e' l'aggancio di %ls, lo lascio com'e'", s.name, path);
        }
    }
}

uint32_t ff::DxgiColorMode(IDXGISwapChain* sc, uint32_t dxgiFormat)
{
    // Ricordato per ogni swapchain: si ricalcola solo se cambiano formato o spazio colore.
    LONG space = KnownSpace(sc);
    uint32_t lastMode = 0xFFFFFFFF;
    AcquireSRWLockShared(&g_swapLock);
    if (SwapInfo* s = FindSwap(sc))
    {
        if (s->format == dxgiFormat && s->space == space)
        {
            uint32_t m = s->mode;
            ReleaseSRWLockShared(&g_swapLock);
            return m;
        }
        if (s->format != 0xFFFFFFFF) lastMode = s->mode;
    }
    ReleaseSRWLockShared(&g_swapLock);

    uint32_t mode = kColorSdr;
    switch ((DXGI_FORMAT)dxgiFormat)
    {
    case DXGI_FORMAT_R16G16B16A16_FLOAT:
        // I 16 bit float di uno swapchain sono sempre lineari (scRGB), qualunque spazio colore.
        mode = kColorScRgb;
        break;
    case DXGI_FORMAT_R10G10B10A2_UNORM:
        // 10 bit: HDR10 se il gioco l'ha chiesto; se non si sa, si guarda se lo schermo è in HDR.
        if (space >= 0) mode = space == DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 ? kColorHdr10 : kColorSdr;
        else mode = OutputIsHdr(sc) ? kColorHdr10 : kColorSdr;
        break;
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
        mode = kColorSdrSrgb;   // vista sRGB: la scheda video ricodifica da sé
        break;
    default:
        break;
    }
    // Nel diario solo quando il modo di uno swapchain cambia, e con un tetto (mai una riga per frame).
    if (mode != lastMode && InterlockedIncrement(&g_modeLogs) <= 32)
        Log("Overlay: colori %s (formato %u, spazio colore %ld)", ColorModeName(mode), dxgiFormat, (long)space);
    AcquireSRWLockExclusive(&g_swapLock);
    if (SwapInfo* s = FindSwap(sc))
    {
        s->format = dxgiFormat;
        s->space = space;
        s->mode = mode;
    }
    ReleaseSRWLockExclusive(&g_swapLock);
    return mode;
}

// Tutti gli swapchain DXGI condividono la stessa tabella di funzioni dentro dxgi.dll:
// ne creiamo uno usa-e-getta per trovarla, poi sostituiamo Present e Present1.
bool ff::HookDxgi(HMODULE self)
{
    HWND hwnd = CreateProbeWindow(self);
    if (!hwnd) return false;

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 1;
    sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    const D3D_DRIVER_TYPE types[] = { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP };
    HRESULT hr = E_FAIL;
    for (D3D_DRIVER_TYPE type : types)
    {
        hr = D3D11CreateDeviceAndSwapChain(nullptr, type, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION,
            &sd, &sc, &dev, nullptr, &ctx);
        if (SUCCEEDED(hr)) break;
    }

    bool ok = false;
    if (SUCCEEDED(hr) && sc)
    {
        void** vtable = *(void***)sc;
        ok = PatchSlot(vtable, kPresentIndex, (void*)HookPresent, (void**)&g_origPresent, "Present");
        // Diario: di chi è la tabella agganciata. Di solito dxgi.dll di Windows; con ReShade o altri
        // "dxgi.dll" messi nella cartella del gioco può essere la loro (si vedono allora i loro Present).
        HMODULE owner = nullptr;
        wchar_t ownerPath[MAX_PATH] = L"?";
        if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                               (LPCWSTR)vtable, &owner) && owner)
            GetModuleFileNameW(owner, ownerPath, MAX_PATH);
        ff::Log("DXGI: tabella di Present in %ls", ownerPath);

        // Present1 esiste solo se lo swapchain implementa IDXGISwapChain1 (Windows 8+).
        IDXGISwapChain1* sc1 = nullptr;
        if (ok && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&sc1)) && sc1)
        {
            void** vtable1 = *(void***)sc1;
            PatchSlot(vtable1, kPresent1Index, (void*)HookPresent1, (void**)&g_origPresent1, "Present1");
            sc1->Release();
        }
        // SetColorSpace1 (Windows 10+): dice se un gioco a 10 bit presenta in HDR10. Senza, si va per deduzione.
        IDXGISwapChain3* sc3 = nullptr;
        if (ok && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&sc3)) && sc3)
        {
            PatchVtable(*(void***)sc3, kSetColorSpace1Index, (void*)HookSetColorSpace1, (void**)&g_origSetColorSpace1);
            sc3->Release();
        }

        // La tabella trovata non e' di Windows: e' di un involucro (ReShade e simili). Si aggancia anche quella
        // vera che sta sotto, da cui passano tutti i fotogrammi (vedi g_origPresentN).
        HMODULE systemDxgi = SystemDxgi();
        if (ok && owner && systemDxgi && owner != systemDxgi)
        {
            IDXGISwapChain* real = FindNative(sc, systemDxgi);
            void** nativeTable = real ? *(void***)real : nullptr;
            if (real && nativeTable != vtable &&
                PatchSlot(nativeTable, kPresentIndex, (void*)HookPresentN, (void**)&g_origPresentN, "Present (Windows)"))
            {
                IDXGISwapChain1* real1 = nullptr;
                if (SUCCEEDED(real->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&real1)) && real1)
                {
                    PatchSlot(*(void***)real1, kPresent1Index, (void*)HookPresent1N, (void**)&g_origPresent1N, "Present1 (Windows)");
                    real1->Release();
                }
                IDXGISwapChain3* real3 = nullptr;
                if (SUCCEEDED(real->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&real3)) && real3)
                {
                    PatchVtable(*(void***)real3, kSetColorSpace1Index, (void*)HookSetColorSpace1N, (void**)&g_origSetColorSpace1N);
                    real3->Release();
                }
                ff::Log("DXGI: agganciata anche la tabella di Windows che sta sotto");
            }
            else
            {
                ff::Log("DXGI: sotto l'involucro non ho trovato lo swapchain di Windows, resto sulla sua tabella");
            }
            if (real) real->Release();
        }
    }

    if (ctx) ctx->Release();
    if (dev) dev->Release();
    if (sc) sc->Release();
    DestroyProbeWindow(hwnd, self);
    return ok;
}
