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
    SetColorSpace1Fn g_origSetColorSpace1 = nullptr;

    HRESULT STDMETHODCALLTYPE HookSetColorSpace1(IDXGISwapChain3* sc, DXGI_COLOR_SPACE_TYPE space)
    {
        HRESULT hr = g_origSetColorSpace1(sc, space);
        if (SUCCEEDED(hr))
        {
            IDXGISwapChain* key = sc;
            SpaceEntry* slot = nullptr;
            for (auto& e : g_spaces)
                if (e.sc == key) { slot = &e; break; }
            if (!slot) slot = &g_spaces[(InterlockedIncrement(&g_spaceNext) - 1) & 15];
            slot->space = (LONG)space;
            MemoryBarrier();
            slot->sc = key;
        }
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
    uint32_t ApiOf(IDXGISwapChain* sc)
    {
        static IDXGISwapChain* known = nullptr;
        static uint32_t knownApi = FF_API_DXGI;
        if (sc == known) return knownApi;

        static const GUID iidD3D10 = { 0x9B7E4C0F, 0x342C, 0x4106, { 0xA1, 0x9F, 0x4F, 0x27, 0x04, 0xF6, 0x89, 0xF0 } };   // ID3D10Device
        static const GUID iidD3D12 = { 0x189819F1, 0x1DB6, 0x4B57, { 0xBE, 0x54, 0x18, 0x21, 0x33, 0x9B, 0x85, 0xF7 } };   // ID3D12Device
        uint32_t api = FF_API_DXGI;
        IUnknown* dev = nullptr;
        if (SUCCEEDED(sc->GetDevice(__uuidof(ID3D11Device), (void**)&dev)) && dev) api = FF_API_D3D11;
        else if (SUCCEEDED(sc->GetDevice(iidD3D12, (void**)&dev)) && dev) api = FF_API_D3D12;
        else if (SUCCEEDED(sc->GetDevice(iidD3D10, (void**)&dev)) && dev) api = FF_API_D3D10;
        if (dev) dev->Release();
        known = sc;
        knownApi = api;
        return api;
    }

    HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* sc, UINT sync, UINT flags)
    {
        if (!(flags & DXGI_PRESENT_TEST))
        {
            if (ff::OwnsFrames(ApiOf(sc))) DrawD3D11(sc);
            ff::OnFrame(ApiOf(sc));
        }
        return g_origPresent(sc, sync, flags);
    }

    HRESULT STDMETHODCALLTYPE HookPresent1(IDXGISwapChain1* sc, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
    {
        if (!(flags & DXGI_PRESENT_TEST))
        {
            if (ff::OwnsFrames(ApiOf(sc))) DrawD3D11(sc);
            ff::OnFrame(ApiOf(sc));
        }
        return g_origPresent1(sc, sync, flags, params);
    }
}

uint32_t ff::DxgiColorMode(IDXGISwapChain* sc, uint32_t dxgiFormat)
{
    // Ricordato per l'ultimo swapchain: si ricalcola solo se cambiano swapchain, formato o spazio colore.
    static IDXGISwapChain* lastSc = nullptr;
    static uint32_t lastFormat = 0xFFFFFFFF, lastMode = kColorSdr;
    static LONG lastSpace = -2;
    LONG space = KnownSpace(sc);
    if (sc == lastSc && dxgiFormat == lastFormat && space == lastSpace) return lastMode;

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
    if (mode != lastMode || sc != lastSc)
        Log("Overlay: colori %s (formato %u, spazio colore %ld)", ColorModeName(mode), dxgiFormat, (long)space);
    lastSc = sc;
    lastFormat = dxgiFormat;
    lastSpace = space;
    lastMode = mode;
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
        ok = PatchVtable(vtable, kPresentIndex, (void*)HookPresent, (void**)&g_origPresent);

        // Present1 esiste solo se lo swapchain implementa IDXGISwapChain1 (Windows 8+).
        IDXGISwapChain1* sc1 = nullptr;
        if (ok && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain1), (void**)&sc1)) && sc1)
        {
            void** vtable1 = *(void***)sc1;
            PatchVtable(vtable1, kPresent1Index, (void*)HookPresent1, (void**)&g_origPresent1);
            sc1->Release();
        }
        // SetColorSpace1 (Windows 10+): dice se un gioco a 10 bit presenta in HDR10. Senza, si va per deduzione.
        IDXGISwapChain3* sc3 = nullptr;
        if (ok && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&sc3)) && sc3)
        {
            PatchVtable(*(void***)sc3, kSetColorSpace1Index, (void*)HookSetColorSpace1, (void**)&g_origSetColorSpace1);
            sc3->Release();
        }
    }

    if (ctx) ctx->Release();
    if (dev) dev->Release();
    if (sc) sc->Release();
    DestroyProbeWindow(hwnd, self);
    return ok;
}
