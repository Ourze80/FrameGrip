// ff_testgame: finto gioco DirectX 11 per provare l'aggancio e il limitatore.
// Disegna a FPS liberi un colore che cambia. Uso: ff_testgame [secondi] [activate] [off] [hdr16 | hdr10 | srgb] [dump]
//   hdr16 / hdr10 / dump: come in ff_testgame12 (dump in %TEMP%\ff_dump_d3d11.bin); srgb: back buffer sRGB
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_4.h>
#include "testdump.h"
#include <cmath>
#include <cstdlib>

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int)
{
    double seconds = (cmd && *cmd) ? _wtof(cmd) : 30.0;
    LoadTestHook();

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FrameGripTestGame";
    RegisterClassExW(&wc);
    // "off": finestra fuori dallo schermo, visibile ma senza fuoco (le prove non disturbano chi usa il PC).
    bool offscreen = cmd && wcsstr(cmd, L"off");
    HWND hwnd = CreateWindowExW(offscreen ? WS_EX_TOOLWINDOW : 0, wc.lpszClassName, L"FrameGrip Test Game", WS_OVERLAPPEDWINDOW,
        offscreen ? -3000 : 120, offscreen ? 100 : 120, offscreen ? 700 : 640, offscreen ? 800 : 360, nullptr, nullptr, inst, nullptr);
    // "ff_testgame 30 activate" porta la finestra in primo piano, come un gioco vero.
    bool activate = cmd && wcsstr(cmd, L"activate");
    ShowWindow(hwnd, activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE);
    if (activate) SetForegroundWindow(hwnd);

    DXGI_SWAP_CHAIN_DESC sd = {};
    sd.BufferCount = 2;
    bool hdr16 = cmd && wcsstr(cmd, L"hdr16"), hdr10 = cmd && wcsstr(cmd, L"hdr10");
    bool dump = cmd && wcsstr(cmd, L"dump"), dumped = false;
    sd.BufferDesc.Format = hdr16 ? DXGI_FORMAT_R16G16B16A16_FLOAT : hdr10 ? DXGI_FORMAT_R10G10B10A2_UNORM : DXGI_FORMAT_R8G8B8A8_UNORM;
    sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    sd.OutputWindow = hwnd;
    sd.SampleDesc.Count = 1;
    sd.Windowed = TRUE;
    // Con "dump" lo swapchain conserva il contenuto dei buffer: dopo il Present il buffer 0 è un
    // fotogramma già presentato (con l'overlay), che si può rileggere.
    sd.SwapEffect = dump ? DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL : DXGI_SWAP_EFFECT_FLIP_DISCARD;
    // "srgb": vecchio swapchain "blt" con formato sRGB (solo questi lo permettono): la vista del back buffer
    // ricodifica da sé, quindi l'overlay deve arrivare lineare (nessun doppio gamma).
    bool srgb = cmd && wcsstr(cmd, L"srgb");
    if (srgb)
    {
        sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
        sd.BufferCount = 1;
        sd.SwapEffect = dump ? DXGI_SWAP_EFFECT_SEQUENTIAL : DXGI_SWAP_EFFECT_DISCARD;
    }

    IDXGISwapChain* sc = nullptr;
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    if (FAILED(D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0,
        D3D11_SDK_VERSION, &sd, &sc, &dev, nullptr, &ctx)))
        return 1;

    ID3D11Texture2D* back = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back);
    dev->CreateRenderTargetView(back, nullptr, &rtv);
    back->Release();
    IDXGISwapChain3* sc3 = nullptr;
    if ((hdr16 || hdr10) && SUCCEEDED(sc->QueryInterface(__uuidof(IDXGISwapChain3), (void**)&sc3)) && sc3)
    {
        HRESULT hr = sc3->SetColorSpace1(hdr16 ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);
        wchar_t path[MAX_PATH];
        GetTempPathW(MAX_PATH, path);
        wcscat_s(path, L"ff_hdr_result.txt");
        FILE* f = nullptr;
        if (_wfopen_s(&f, path, L"w") == 0 && f) { fprintf(f, "d3d11 format %d SetColorSpace1 hr=0x%08lX\n", (int)sd.BufferDesc.Format, (unsigned long)hr); fclose(f); }
    }

    ULONGLONG start = GetTickCount64();
    MSG msg = {};
    bool running = true;
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

        // Lo spazio colore ridetto a 4 s, ad aggancio avvenuto ("nocs": no).
        static bool spaceAgain = false;
        if (sc3 && !spaceAgain && t > 4.0 && !wcsstr(cmd, L"nocs"))
        {
            spaceAgain = true;
            sc3->SetColorSpace1(hdr16 ? DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 : DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020);
        }
        float color[4] = { (float)(0.5 + 0.5 * sin(t)), (float)(0.5 + 0.5 * sin(t * 1.3)), 0.4f, 1.0f };
        if (dump) color[0] = color[1] = color[2] = 0.0f;   // fondo nero: valori attesi esatti
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        ctx->ClearRenderTargetView(rtv, color);
        sc->Present(0, 0);

        if (dump && !dumped && t > seconds - 1.5)
        {
            dumped = true;
            ID3D11Texture2D* shown = nullptr;
            ID3D11Texture2D* staging = nullptr;
            sc->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&shown);
            D3D11_TEXTURE2D_DESC td;
            shown->GetDesc(&td);
            td.Usage = D3D11_USAGE_STAGING;
            td.BindFlags = 0;
            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            td.MiscFlags = 0;
            D3D11_MAPPED_SUBRESOURCE map;
            if (SUCCEEDED(dev->CreateTexture2D(&td, nullptr, &staging)))
            {
                ctx->CopyResource(staging, shown);
                if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &map)))
                {
                    WriteTestDump(L"d3d11", map.pData, td.Width, td.Height, (uint32_t)td.Format, hdr16 ? 8 : 4, map.RowPitch);
                    ctx->Unmap(staging, 0);
                }
                staging->Release();
            }
            shown->Release();
        }
    }

    if (sc3) sc3->Release();
    rtv->Release();
    ctx->Release();
    dev->Release();
    sc->Release();
    return 0;
}
