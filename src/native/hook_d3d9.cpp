// Aggancio a Direct3D 9 (la maggior parte dei giochi più vecchi, spesso a 32 bit):
// limitatore, misura e disegno dell'overlay.
#include "common.h"
#include <d3d9.h>
#include <cstring>
#include "third_party/minhook/include/MinHook.h"

namespace
{
    // Indici nelle tabelle di funzioni di d3d9.
    const int kReset = 16, kPresent = 17, kPresentEx = 121, kResetEx = 132;
    const int kSwapChainPresent = 3;

    typedef HRESULT(STDMETHODCALLTYPE* PresentFn)(IDirect3DDevice9*, const RECT*, const RECT*, HWND, const RGNDATA*);
    typedef HRESULT(STDMETHODCALLTYPE* ResetFn)(IDirect3DDevice9*, D3DPRESENT_PARAMETERS*);
    typedef HRESULT(STDMETHODCALLTYPE* PresentExFn)(IDirect3DDevice9Ex*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);
    typedef HRESULT(STDMETHODCALLTYPE* ResetExFn)(IDirect3DDevice9Ex*, D3DPRESENT_PARAMETERS*, D3DDISPLAYMODEEX*);
    typedef HRESULT(STDMETHODCALLTYPE* ScPresentFn)(IDirect3DSwapChain9*, const RECT*, const RECT*, HWND, const RGNDATA*, DWORD);

    // In d3d9 ogni device ha una copia propria della tabella di funzioni, quindi sostituire le voci
    // di un device di prova non serve: si devia direttamente il codice delle funzioni, che è
    // condiviso da tutti i device ("aggancio inline").
    PresentFn g_present = nullptr;
    ResetFn g_reset = nullptr;
    PresentExFn g_presentEx = nullptr;
    ResetExFn g_resetEx = nullptr;
    ScPresentFn g_scPresent = nullptr;
    void* g_presentAddr = nullptr;
    void* g_resetAddr = nullptr;
    void* g_presentExAddr = nullptr;
    void* g_resetExAddr = nullptr;
    void* g_scPresentAddr = nullptr;
    thread_local int g_depth = 0;   // evita di contare due volte se un Present ne chiama un altro

    // Devia una funzione una sola volta (con MinHook, a 32 e 64 bit); se compare una seconda
    // variante la segnala nel diario.
    template <typename Fn>
    void HookOnce(const char* name, void* target, void* hook, Fn& original, void*& hookedAddr)
    {
        if (!target || target == hookedAddr) return;
        if (hookedAddr) return;   // una variante diversa della stessa funzione: basta la prima
        if (ff::InlineHook(name, target, hook, (void**)&original)) hookedAddr = target;
    }

    // ---------------------------------------------------------------- disegno

    IDirect3DDevice9* g_dev = nullptr;        // solo per confronto
    IDirect3DTexture9* g_tex = nullptr;
    IDirect3DStateBlock9* g_state = nullptr;
    uint32_t g_texW = 0, g_texH = 0, g_texSerial = 0;

    void ReleaseResources()
    {
        if (g_tex) { g_tex->Release(); g_tex = nullptr; }
        if (g_state) { g_state->Release(); g_state = nullptr; }
        g_texW = g_texH = 0;
        g_dev = nullptr;
    }

    struct Vertex { float x, y, z, rhw, u, v; };

    void Draw(IDirect3DDevice9* dev, IDirect3DSurface9* back)
    {
        ff::Overlay ov;
        if (!dev || !back || !ff::GetOverlay(ov)) return;
        if (dev->TestCooperativeLevel() != D3D_OK) return;   // device perso (es. alt-tab a schermo intero)

        if (dev != g_dev)
        {
            ReleaseResources();
            g_dev = dev;
        }

        if (!g_tex || g_texW != ov.width || g_texH != ov.height)
        {
            if (g_tex) { g_tex->Release(); g_tex = nullptr; }
            if (FAILED(dev->CreateTexture(ov.width, ov.height, 1, D3DUSAGE_DYNAMIC, D3DFMT_A8R8G8B8,
                D3DPOOL_DEFAULT, &g_tex, nullptr)))
                return;
            g_texW = ov.width;
            g_texH = ov.height;
            g_texSerial = ov.serial - 1;
        }
        if (g_texSerial != ov.serial)
        {
            D3DLOCKED_RECT lr;
            if (FAILED(g_tex->LockRect(0, &lr, nullptr, D3DLOCK_DISCARD))) return;
            const uint8_t* src = ov.pixels;
            uint8_t* dst = (uint8_t*)lr.pBits;
            size_t row = (size_t)ov.width * 4;
            for (uint32_t y = 0; y < ov.height; y++, src += row, dst += lr.Pitch) memcpy(dst, src, row);
            g_tex->UnlockRect(0);
            g_texSerial = ov.serial;
        }

        if (!g_state && FAILED(dev->CreateStateBlock(D3DSBT_ALL, &g_state))) return;

        D3DSURFACE_DESC bd;
        if (FAILED(back->GetDesc(&bd))) return;
        int x, y;
        ff::Place(ov, bd.Width, bd.Height, x, y);

        // Lo state block non salva i bersagli di disegno: quelli a mano.
        IDirect3DSurface9* oldTarget = nullptr;
        IDirect3DSurface9* oldDepth = nullptr;
        dev->GetRenderTarget(0, &oldTarget);
        dev->GetDepthStencilSurface(&oldDepth);
        g_state->Capture();

        dev->SetRenderTarget(0, back);
        dev->SetDepthStencilSurface(nullptr);
        D3DVIEWPORT9 vp = { 0, 0, bd.Width, bd.Height, 0.0f, 1.0f };
        dev->SetViewport(&vp);

        dev->SetPixelShader(nullptr);
        dev->SetVertexShader(nullptr);
        dev->SetFVF(D3DFVF_XYZRHW | D3DFVF_TEX1);
        dev->SetTexture(0, g_tex);
        dev->SetTexture(1, nullptr);

        dev->SetRenderState(D3DRS_LIGHTING, FALSE);
        dev->SetRenderState(D3DRS_ZENABLE, D3DZB_FALSE);
        dev->SetRenderState(D3DRS_ZWRITEENABLE, FALSE);
        dev->SetRenderState(D3DRS_CULLMODE, D3DCULL_NONE);
        dev->SetRenderState(D3DRS_FILLMODE, D3DFILL_SOLID);
        dev->SetRenderState(D3DRS_ALPHATESTENABLE, FALSE);
        dev->SetRenderState(D3DRS_ALPHABLENDENABLE, TRUE);
        dev->SetRenderState(D3DRS_SEPARATEALPHABLENDENABLE, FALSE);
        dev->SetRenderState(D3DRS_BLENDOP, D3DBLENDOP_ADD);
        dev->SetRenderState(D3DRS_SRCBLEND, D3DBLEND_ONE);            // alfa premoltiplicato
        dev->SetRenderState(D3DRS_DESTBLEND, D3DBLEND_INVSRCALPHA);
        dev->SetRenderState(D3DRS_SCISSORTESTENABLE, FALSE);
        dev->SetRenderState(D3DRS_STENCILENABLE, FALSE);
        dev->SetRenderState(D3DRS_FOGENABLE, FALSE);
        dev->SetRenderState(D3DRS_CLIPPING, TRUE);
        dev->SetRenderState(D3DRS_SRGBWRITEENABLE, FALSE);
        dev->SetRenderState(D3DRS_COLORWRITEENABLE, 0x0000000F);

        dev->SetTextureStageState(0, D3DTSS_COLOROP, D3DTOP_SELECTARG1);
        dev->SetTextureStageState(0, D3DTSS_COLORARG1, D3DTA_TEXTURE);
        dev->SetTextureStageState(0, D3DTSS_ALPHAOP, D3DTOP_SELECTARG1);
        dev->SetTextureStageState(0, D3DTSS_ALPHAARG1, D3DTA_TEXTURE);
        dev->SetTextureStageState(0, D3DTSS_TEXTURETRANSFORMFLAGS, D3DTTFF_DISABLE);
        dev->SetTextureStageState(1, D3DTSS_COLOROP, D3DTOP_DISABLE);
        dev->SetTextureStageState(1, D3DTSS_ALPHAOP, D3DTOP_DISABLE);
        dev->SetSamplerState(0, D3DSAMP_MINFILTER, D3DTEXF_POINT);
        dev->SetSamplerState(0, D3DSAMP_MAGFILTER, D3DTEXF_POINT);
        dev->SetSamplerState(0, D3DSAMP_MIPFILTER, D3DTEXF_NONE);
        dev->SetSamplerState(0, D3DSAMP_ADDRESSU, D3DTADDRESS_CLAMP);
        dev->SetSamplerState(0, D3DSAMP_ADDRESSV, D3DTADDRESS_CLAMP);
        dev->SetSamplerState(0, D3DSAMP_SRGBTEXTURE, FALSE);

        // In D3D9 il centro del pixel è spostato di mezzo pixel rispetto al texel.
        float l = (float)x - 0.5f, t = (float)y - 0.5f;
        float r = l + (float)ov.width, b = t + (float)ov.height;
        const Vertex quad[4] =
        {
            { l, t, 0.0f, 1.0f, 0.0f, 0.0f },
            { r, t, 0.0f, 1.0f, 1.0f, 0.0f },
            { l, b, 0.0f, 1.0f, 0.0f, 1.0f },
            { r, b, 0.0f, 1.0f, 1.0f, 1.0f },
        };
        if (SUCCEEDED(dev->BeginScene()))
        {
            dev->DrawPrimitiveUP(D3DPT_TRIANGLESTRIP, 2, quad, sizeof(Vertex));
            dev->EndScene();
            ff::MarkDrawn();
        }

        g_state->Apply();
        dev->SetRenderTarget(0, oldTarget);
        dev->SetDepthStencilSurface(oldDepth);
        if (oldTarget) oldTarget->Release();
        if (oldDepth) oldDepth->Release();
    }

    void Adopt(IDirect3DDevice9* dev);

    void FrameFromDevice(IDirect3DDevice9* dev)
    {
        Adopt(dev);
        IDirect3DSurface9* back = nullptr;
        if (SUCCEEDED(dev->GetBackBuffer(0, 0, D3DBACKBUFFER_TYPE_MONO, &back)) && back)
        {
            Draw(dev, back);
            back->Release();
        }
        ff::OnFrame(FF_API_D3D9);
    }

    // ---------------------------------------------------------------- funzioni sostitutive

    HRESULT STDMETHODCALLTYPE HookPresent(IDirect3DDevice9* dev, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty)
    {
        if (g_depth == 0) FrameFromDevice(dev);
        g_depth++;
        HRESULT hr = g_present(dev, src, dst, wnd, dirty);
        g_depth--;
        return hr;
    }

    HRESULT STDMETHODCALLTYPE HookPresentEx(IDirect3DDevice9Ex* dev, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty, DWORD flags)
    {
        if (g_depth == 0) FrameFromDevice(dev);
        g_depth++;
        HRESULT hr = g_presentEx(dev, src, dst, wnd, dirty, flags);
        g_depth--;
        return hr;
    }

    HRESULT STDMETHODCALLTYPE HookSwapChainPresent(IDirect3DSwapChain9* sc, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty, DWORD flags)
    {
        if (g_depth == 0)
        {
            IDirect3DDevice9* dev = nullptr;
            IDirect3DSurface9* back = nullptr;
            if (SUCCEEDED(sc->GetDevice(&dev)) && dev)
            {
                if (SUCCEEDED(sc->GetBackBuffer(0, D3DBACKBUFFER_TYPE_MONO, &back)) && back)
                {
                    Draw(dev, back);
                    back->Release();
                }
                dev->Release();
            }
            ff::OnFrame(FF_API_D3D9);
        }
        g_depth++;
        HRESULT hr = g_scPresent(sc, src, dst, wnd, dirty, flags);
        g_depth--;
        return hr;
    }

    // Prima di un Reset il gioco deve aver liberato tutte le risorse "volatili": anche le nostre.
    HRESULT STDMETHODCALLTYPE HookReset(IDirect3DDevice9* dev, D3DPRESENT_PARAMETERS* pp)
    {
        if (dev == g_dev) ReleaseResources();
        return g_reset(dev, pp);
    }

    HRESULT STDMETHODCALLTYPE HookResetEx(IDirect3DDevice9Ex* dev, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* mode)
    {
        if ((IDirect3DDevice9*)dev == g_dev) ReleaseResources();
        return g_resetEx(dev, pp, mode);
    }

    // ---------------------------------------------------------------- aggancio sul singolo device
    //
    // Alcuni overlay di altri programmi cancellano a ogni frame le deviazioni altrui
    // sul codice di Present. La deviazione ci serve quindi solo per "vedere" una volta il device
    // del gioco: da lì in poi sostituiamo le voci della SUA tabella di funzioni, che nessuno tocca.

    struct DeviceTable
    {
        void** vtable;
        PresentFn present;
        ResetFn reset;
        PresentExFn presentEx;
        ResetExFn resetEx;
    };
    struct SwapChainTable
    {
        void** vtable;
        ScPresentFn present;
    };
    DeviceTable g_tables[8];
    int g_tableCount = 0;
    SwapChainTable g_scTables[8];
    int g_scTableCount = 0;

    DeviceTable* TableOf(void* object)
    {
        void** vt = *(void***)object;
        for (int i = 0; i < g_tableCount; i++)
            if (g_tables[i].vtable == vt) return &g_tables[i];
        return nullptr;
    }

    SwapChainTable* ScTableOf(void* object)
    {
        void** vt = *(void***)object;
        for (int i = 0; i < g_scTableCount; i++)
            if (g_scTables[i].vtable == vt) return &g_scTables[i];
        return nullptr;
    }

    HRESULT STDMETHODCALLTYPE VtPresent(IDirect3DDevice9* dev, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty)
    {
        DeviceTable* t = TableOf(dev);
        if (g_depth == 0) FrameFromDevice(dev);
        g_depth++;
        HRESULT hr = t->present(dev, src, dst, wnd, dirty);
        g_depth--;
        return hr;
    }

    HRESULT STDMETHODCALLTYPE VtPresentEx(IDirect3DDevice9Ex* dev, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty, DWORD flags)
    {
        DeviceTable* t = TableOf(dev);
        if (g_depth == 0) FrameFromDevice(dev);
        g_depth++;
        HRESULT hr = t->presentEx(dev, src, dst, wnd, dirty, flags);
        g_depth--;
        return hr;
    }

    HRESULT STDMETHODCALLTYPE VtReset(IDirect3DDevice9* dev, D3DPRESENT_PARAMETERS* pp)
    {
        if (dev == g_dev) ReleaseResources();
        return TableOf(dev)->reset(dev, pp);
    }

    HRESULT STDMETHODCALLTYPE VtResetEx(IDirect3DDevice9Ex* dev, D3DPRESENT_PARAMETERS* pp, D3DDISPLAYMODEEX* mode)
    {
        if ((IDirect3DDevice9*)dev == g_dev) ReleaseResources();
        return TableOf(dev)->resetEx(dev, pp, mode);
    }

    HRESULT STDMETHODCALLTYPE VtSwapChainPresent(IDirect3DSwapChain9* sc, const RECT* src, const RECT* dst, HWND wnd, const RGNDATA* dirty, DWORD flags)
    {
        SwapChainTable* t = ScTableOf(sc);
        if (g_depth == 0)
        {
            IDirect3DDevice9* dev = nullptr;
            IDirect3DSurface9* back = nullptr;
            if (SUCCEEDED(sc->GetDevice(&dev)) && dev)
            {
                if (SUCCEEDED(sc->GetBackBuffer(0, D3DBACKBUFFER_TYPE_MONO, &back)) && back)
                {
                    Draw(dev, back);
                    back->Release();
                }
                dev->Release();
            }
            ff::OnFrame(FF_API_D3D9);
        }
        g_depth++;
        HRESULT hr = t->present(sc, src, dst, wnd, dirty, flags);
        g_depth--;
        return hr;
    }

    void Adopt(IDirect3DDevice9* dev)
    {
        if (TableOf(dev) || g_tableCount >= 8) return;

        void** vt = *(void***)dev;
        IDirect3DDevice9Ex* ex = nullptr;
        bool isEx = SUCCEEDED(dev->QueryInterface(__uuidof(IDirect3DDevice9Ex), (void**)&ex)) && ex;
        if (ex) ex->Release();

        // La tabella va registrata prima di sostituire le voci: da quel momento arrivano chiamate.
        DeviceTable& t = g_tables[g_tableCount];
        t = {};
        t.vtable = vt;
        t.present = (PresentFn)vt[kPresent];
        t.reset = (ResetFn)vt[kReset];
        if (isEx)
        {
            t.presentEx = (PresentExFn)vt[kPresentEx];
            t.resetEx = (ResetExFn)vt[kResetEx];
        }
        g_tableCount++;

        void* unused;
        ff::PatchVtable(vt, kPresent, (void*)VtPresent, &unused);
        ff::PatchVtable(vt, kReset, (void*)VtReset, &unused);
        if (isEx)
        {
            ff::PatchVtable(vt, kPresentEx, (void*)VtPresentEx, &unused);
            ff::PatchVtable(vt, kResetEx, (void*)VtResetEx, &unused);
        }

        IDirect3DSwapChain9* sc = nullptr;
        if (SUCCEEDED(dev->GetSwapChain(0, &sc)) && sc)
        {
            if (!ScTableOf(sc) && g_scTableCount < 8)
            {
                void** svt = *(void***)sc;
                SwapChainTable& st = g_scTables[g_scTableCount];
                st.vtable = svt;
                st.present = (ScPresentFn)svt[kSwapChainPresent];
                g_scTableCount++;
                ff::PatchVtable(svt, kSwapChainPresent, (void*)VtSwapChainPresent, &unused);
            }
            sc->Release();
        }
        ff::Log("D3D9: device del gioco %p agganciato sulla sua tabella di funzioni%s", dev, isEx ? " (Ex)" : "");
    }

    // ---------------------------------------------------------------- aggancio

    void PatchDevice(IDirect3DDevice9* dev, bool isEx)
    {
        // Dal device di prova si ricava solo l'indirizzo del codice delle funzioni.
        void** vt = *(void***)dev;
        HookOnce("D3D9 Present", vt[kPresent], (void*)HookPresent, g_present, g_presentAddr);
        HookOnce("D3D9 Reset", vt[kReset], (void*)HookReset, g_reset, g_resetAddr);
        if (isEx)
        {
            HookOnce("D3D9 PresentEx", vt[kPresentEx], (void*)HookPresentEx, g_presentEx, g_presentExAddr);
            HookOnce("D3D9 ResetEx", vt[kResetEx], (void*)HookResetEx, g_resetEx, g_resetExAddr);
        }

        IDirect3DSwapChain9* sc = nullptr;
        if (SUCCEEDED(dev->GetSwapChain(0, &sc)) && sc)
        {
            void** svt = *(void***)sc;
            HookOnce("D3D9 SwapChain Present", svt[kSwapChainPresent], (void*)HookSwapChainPresent, g_scPresent, g_scPresentAddr);
            sc->Release();
        }
    }
}

bool ff::HookD3D9(HMODULE self)
{
    HWND hwnd = CreateProbeWindow(self);
    if (!hwnd) return false;

    D3DPRESENT_PARAMETERS pp = {};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.BackBufferWidth = 8;
    pp.BackBufferHeight = 8;
    pp.hDeviceWindow = hwnd;

    // Niente D3DCREATE_MULTITHREADED: quei device ricevono una tabella di funzioni propria per
    // ogni istanza, inutile come modello. (I giochi che li usano richiedono un aggancio "inline",
    // non ancora presente.)
    const DWORD common = D3DCREATE_NOWINDOWCHANGES | D3DCREATE_FPU_PRESERVE;
    const DWORD variants[] =
    {
        common | D3DCREATE_SOFTWARE_VERTEXPROCESSING,
        common | D3DCREATE_HARDWARE_VERTEXPROCESSING,
        common | D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_PUREDEVICE,
    };

    // Device Ex (Vista in poi): la loro tabella contiene anche PresentEx e ResetEx.
    IDirect3D9Ex* d3dEx = nullptr;
    if (SUCCEEDED(Direct3DCreate9Ex(D3D_SDK_VERSION, &d3dEx)) && d3dEx)
    {
        for (DWORD flags : variants)
        {
            D3DPRESENT_PARAMETERS p = pp;
            IDirect3DDevice9Ex* dev = nullptr;
            HRESULT hr = d3dEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd, flags, &p, nullptr, &dev);
            ff::Log("D3D9Ex device di prova (flag %08lX): hr=%08lX tabella=%p", flags, hr, dev ? *(void**)dev : nullptr);
            if (SUCCEEDED(hr) && dev)
            {
                PatchDevice(dev, true);
                dev->Release();
            }
        }
        d3dEx->Release();
    }

    // Device classici.
    IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (d3d)
    {
        for (DWORD flags : variants)
        {
            D3DPRESENT_PARAMETERS p = pp;
            IDirect3DDevice9* dev = nullptr;
            HRESULT hr = d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd, flags, &p, &dev);
            ff::Log("D3D9 device di prova (flag %08lX): hr=%08lX tabella=%p", flags, hr, dev ? *(void**)dev : nullptr);
            if (SUCCEEDED(hr) && dev)
            {
                PatchDevice(dev, false);
                dev->Release();
            }
        }
        d3d->Release();
    }

    DestroyProbeWindow(hwnd, self);
    return g_presentAddr != nullptr;
}
