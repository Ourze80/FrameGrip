// ff_testgamegl: finto gioco OpenGL classico (compilato a 32 bit) per provare i giochi più vecchi.
// Uso: ff_testgamegl [secondi] [activate] [off] [dump]
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <gl/GL.h>
#include <cmath>
#include <cstdlib>
#include <cwchar>
#include "testdump.h"
#include <cstdio>

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l)
{
    if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
    return DefWindowProcW(h, m, w, l);
}

int WINAPI wWinMain(HINSTANCE inst, HINSTANCE, PWSTR cmd, int)
{
    double seconds = (cmd && *cmd) ? _wtof(cmd) : 30.0;

    WNDCLASSEXW wc = { sizeof(wc) };
    wc.style = CS_OWNDC;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = inst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.lpszClassName = L"FrameGripTestGameGL";
    RegisterClassExW(&wc);
    // "off": finestra fuori dallo schermo, visibile ma senza fuoco (le prove non disturbano chi usa il PC).
    bool offscreen = TestOffscreen(cmd);
    HWND hwnd = CreateWindowExW(offscreen ? WS_EX_TOOLWINDOW : 0, wc.lpszClassName, L"FrameGrip Test Game (OpenGL, 32 bit)", WS_OVERLAPPEDWINDOW,
        offscreen ? -3000 : 200, offscreen ? 100 : 200, offscreen ? 700 : 640, offscreen ? 800 : 360, nullptr, nullptr, inst, nullptr);
    bool activate = cmd && wcsstr(cmd, L"activate");
    ShowWindow(hwnd, activate ? SW_SHOWNORMAL : SW_SHOWNOACTIVATE);
    if (activate) SetForegroundWindow(hwnd);

    HDC dc = GetDC(hwnd);
    PIXELFORMATDESCRIPTOR pfd = { sizeof(pfd), 1 };
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    pfd.cDepthBits = 24;
    SetPixelFormat(dc, ChoosePixelFormat(dc, &pfd), &pfd);
    HGLRC rc = wglCreateContext(dc);
    if (!rc || !wglMakeCurrent(dc, rc)) return 1;

    bool dump = cmd && wcsstr(cmd, L"dump"), dumped = false;
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

        glClearColor((float)(0.5 + 0.5 * sin(t)), 0.35f, (float)(0.5 + 0.5 * sin(t * 1.3)), 1.0f);
        glClear(GL_COLOR_BUFFER_BIT | GL_DEPTH_BUFFER_BIT);
        glBegin(GL_TRIANGLES);   // un triangolo, per lasciare lo stato "sporco" come farebbe un gioco
        glColor3f(1, 1, 0); glVertex2f(-0.5f, -0.5f);
        glColor3f(0, 1, 1); glVertex2f(0.5f, -0.5f);
        glColor3f(1, 0, 1); glVertex2f(0.0f, 0.5f);
        glEnd();
        SwapBuffers(dc);

        // "dump": verso la fine, l'immagine appena presentata (con l'overlay disegnato dalla DLL dentro
        // SwapBuffers) finisce in %TEMP%\ff_dump_gl.bmp, per controllare l'overlay senza catturare lo schermo.
        if (dump && !dumped && t > seconds - 1.5)
        {
            dumped = true;
            RECT rc2;
            GetClientRect(hwnd, &rc2);
            int w = rc2.right, h = rc2.bottom;
            void* px = malloc((size_t)w * h * 4);
            glFinish();
            glReadBuffer(GL_FRONT);
            glReadPixels(0, 0, w, h, 0x80E1 /*GL_BGRA_EXT*/, GL_UNSIGNED_BYTE, px);
            wchar_t path[MAX_PATH];
            GetTempPathW(MAX_PATH, path);
            wcscat_s(path, L"ff_dump_gl.bmp");
            FILE* f = nullptr;
            if (px && _wfopen_s(&f, path, L"wb") == 0 && f)
            {
                BITMAPFILEHEADER fh = { 0x4D42, (DWORD)(54 + w * h * 4), 0, 0, 54 };
                BITMAPINFOHEADER ih = { sizeof(ih), w, h, 1, 32, BI_RGB };
                fwrite(&fh, sizeof(fh), 1, f);
                fwrite(&ih, sizeof(ih), 1, f);
                fwrite(px, 4, (size_t)w * h, f);   // OpenGL legge dal basso: come una bitmap "dal basso"
                fclose(f);
            }
            free(px);
        }
    }

    wglMakeCurrent(nullptr, nullptr);
    wglDeleteContext(rc);
    return 0;
}
