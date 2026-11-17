// ff_testgame9: finto gioco Direct3D 9 (compilato a 32 bit) per provare i giochi più vecchi.
// Uso: ff_testgame9 [secondi] [activate] [off]
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d9.h>
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

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FrameGripTestGame9";
    RegisterClassExW(&wc);
    // "off": finestra fuori dallo schermo, visibile ma senza fuoco (le prove non disturbano chi usa il PC).
    bool offscreen = cmd && wcsstr(cmd, L"off");
    HWND hwnd = CreateWindowExW(offscreen ? WS_EX_TOOLWINDOW : 0, wc.lpszClassName, L"FrameGrip Test Game (D3D9, 32 bit)", WS_OVERLAPPEDWINDOW,
        offscreen ? -3000 : 160, offscreen ? 100 : 160, offscreen ? 700 : 640, offscreen ? 800 : 360, nullptr, nullptr, inst, nullptr);
    bool activate = cmd && wcsstr(cmd, L"activate");
    ShowWindow(hwnd, activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE);
    if (activate) SetForegroundWindow(hwnd);

    IDirect3D9* d3d = Direct3DCreate9(D3D_SDK_VERSION);
    if (!d3d) return 1;
    D3DPRESENT_PARAMETERS pp = {};
    pp.Windowed = TRUE;
    pp.SwapEffect = D3DSWAPEFFECT_DISCARD;
    pp.BackBufferFormat = D3DFMT_UNKNOWN;
    pp.hDeviceWindow = hwnd;
    pp.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    IDirect3DDevice9* dev = nullptr;
    if (FAILED(d3d->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, hwnd,
        D3DCREATE_HARDWARE_VERTEXPROCESSING, &pp, &dev)))
        return 2;

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

        int r = (int)(127 + 127 * sin(t)), g = (int)(127 + 127 * sin(t * 1.3));
        dev->Clear(0, nullptr, D3DCLEAR_TARGET, D3DCOLOR_XRGB(r, g, 100), 1.0f, 0);
        dev->BeginScene();
        dev->EndScene();
        dev->Present(nullptr, nullptr, nullptr, nullptr);
    }

    dev->Release();
    d3d->Release();
    return 0;
}
