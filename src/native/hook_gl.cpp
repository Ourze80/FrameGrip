// Aggancio a OpenGL: limitatore, misura e disegno dell'overlay.
// Il disegno usa le funzioni classiche di OpenGL, quindi funziona con i contesti "compatibili"
// (tutti i giochi più vecchi). Con i contesti "core" restano attivi limitatore e misura.
#include "common.h"
#include <gl/GL.h>
#include <cstring>
#include "third_party/minhook/include/MinHook.h"

#define FF_GL_BGRA 0x80E1
#define FF_GL_CLAMP_TO_EDGE 0x812F
#define FF_GL_CURRENT_PROGRAM 0x8B8D
#define FF_GL_PIXEL_UNPACK_BUFFER 0x88EC
#define FF_GL_PIXEL_UNPACK_BUFFER_BINDING 0x88EF
#define FF_GL_CONTEXT_PROFILE_MASK 0x9126
#define FF_GL_FRAMEBUFFER 0x8D40
#define FF_GL_FRAMEBUFFER_BINDING 0x8CA6
#define FF_GL_TEXTURE0 0x84C0

namespace
{
    typedef BOOL(WINAPI* SwapFn)(HDC);
    typedef void(APIENTRY* UseProgramFn)(GLuint);
    typedef void(APIENTRY* BindBufferFn)(GLenum, GLuint);
    typedef void(APIENTRY* BindFramebufferFn)(GLenum, GLuint);
    typedef void(APIENTRY* ActiveTextureFn)(GLenum);

    SwapFn g_origSwap = nullptr;
    HGLRC g_ctx = nullptr;
    GLuint g_tex = 0;
    uint32_t g_texW = 0, g_texH = 0, g_texSerial = 0;
    thread_local int g_depth = 0;

    void Draw(HDC hdc)
    {
        ff::Overlay ov;
        if (!ff::GetOverlay(ov)) return;
        HGLRC ctx = wglGetCurrentContext();
        if (!ctx) return;

        // Nei contesti "core" le funzioni classiche non esistono: lì non si disegna.
        GLint profile = 0;
        glGetIntegerv(FF_GL_CONTEXT_PROFILE_MASK, &profile);
        while (glGetError() != GL_NO_ERROR) {}
        if (profile & 0x1) return;

        HWND hwnd = WindowFromDC(hdc);
        RECT rc;
        if (!hwnd || !GetClientRect(hwnd, &rc) || rc.right <= 0 || rc.bottom <= 0) return;

        if (ctx != g_ctx)
        {
            g_ctx = ctx;
            g_tex = 0;
            g_texW = g_texH = 0;
        }

        auto useProgram = (UseProgramFn)wglGetProcAddress("glUseProgram");
        auto bindBuffer = (BindBufferFn)wglGetProcAddress("glBindBuffer");
        auto bindFramebuffer = (BindFramebufferFn)wglGetProcAddress("glBindFramebuffer");
        auto activeTexture = (ActiveTextureFn)wglGetProcAddress("glActiveTexture");

        GLint program = 0, unpackBuffer = 0, framebuffer = 0;
        if (useProgram) glGetIntegerv(FF_GL_CURRENT_PROGRAM, &program);
        if (bindBuffer) glGetIntegerv(FF_GL_PIXEL_UNPACK_BUFFER_BINDING, &unpackBuffer);
        if (bindFramebuffer) glGetIntegerv(FF_GL_FRAMEBUFFER_BINDING, &framebuffer);

        glPushAttrib(GL_ALL_ATTRIB_BITS);
        glPushClientAttrib(GL_CLIENT_ALL_ATTRIB_BITS);
        if (useProgram) useProgram(0);
        if (bindBuffer) bindBuffer(FF_GL_PIXEL_UNPACK_BUFFER, 0);
        if (bindFramebuffer) bindFramebuffer(FF_GL_FRAMEBUFFER, 0);
        if (activeTexture) activeTexture(FF_GL_TEXTURE0);

        glMatrixMode(GL_TEXTURE);    glPushMatrix(); glLoadIdentity();
        glMatrixMode(GL_PROJECTION); glPushMatrix(); glLoadIdentity();
        glOrtho(0, rc.right, rc.bottom, 0, -1, 1);
        glMatrixMode(GL_MODELVIEW);  glPushMatrix(); glLoadIdentity();

        glViewport(0, 0, rc.right, rc.bottom);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_CULL_FACE);
        glDisable(GL_LIGHTING);
        glDisable(GL_SCISSOR_TEST);
        glDisable(GL_ALPHA_TEST);
        glDisable(GL_STENCIL_TEST);
        glDisable(GL_FOG);
        glColorMask(GL_TRUE, GL_TRUE, GL_TRUE, GL_TRUE);
        glPolygonMode(GL_FRONT_AND_BACK, GL_FILL);
        glEnable(GL_BLEND);
        glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA);   // alfa premoltiplicato
        glEnable(GL_TEXTURE_2D);
        glTexEnvi(GL_TEXTURE_ENV, GL_TEXTURE_ENV_MODE, GL_REPLACE);

        if (!g_tex) glGenTextures(1, &g_tex);
        glBindTexture(GL_TEXTURE_2D, g_tex);
        if (g_texW == ov.width && g_texH == ov.height && g_texSerial != ov.serial)
        {
            // Stessa misura: si ricaricano solo le righe cambiate (di solito la striscia del grafico).
            uint32_t y0, y1;
            ff::RowsToUpload(ov, g_texSerial, false, y0, y1);
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
            glPixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
            glPixelStorei(GL_UNPACK_SKIP_ROWS, 0);
            glTexSubImage2D(GL_TEXTURE_2D, 0, 0, y0, ov.width, y1 - y0, FF_GL_BGRA, GL_UNSIGNED_BYTE, ov.pixels + (size_t)y0 * ov.width * 4);
            g_texSerial = ov.serial;
        }
        else if (g_texW != ov.width || g_texH != ov.height)
        {
            glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
            glPixelStorei(GL_UNPACK_ROW_LENGTH, 0);
            glPixelStorei(GL_UNPACK_SKIP_PIXELS, 0);
            glPixelStorei(GL_UNPACK_SKIP_ROWS, 0);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, FF_GL_CLAMP_TO_EDGE);
            glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, FF_GL_CLAMP_TO_EDGE);
            glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, ov.width, ov.height, 0, FF_GL_BGRA, GL_UNSIGNED_BYTE, ov.pixels);
            g_texW = ov.width;
            g_texH = ov.height;
            g_texSerial = ov.serial;
        }

        int x, y;
        ff::Place(ov, (uint32_t)rc.right, (uint32_t)rc.bottom, x, y);
        float l = (float)x, t = (float)y, r = l + ov.width, b = t + ov.height;
        glBegin(GL_QUADS);
        glTexCoord2f(0, 0); glVertex2f(l, t);
        glTexCoord2f(1, 0); glVertex2f(r, t);
        glTexCoord2f(1, 1); glVertex2f(r, b);
        glTexCoord2f(0, 1); glVertex2f(l, b);
        glEnd();

        glMatrixMode(GL_MODELVIEW);  glPopMatrix();
        glMatrixMode(GL_PROJECTION); glPopMatrix();
        glMatrixMode(GL_TEXTURE);    glPopMatrix();
        if (bindFramebuffer) bindFramebuffer(FF_GL_FRAMEBUFFER, (GLuint)framebuffer);
        if (bindBuffer) bindBuffer(FF_GL_PIXEL_UNPACK_BUFFER, (GLuint)unpackBuffer);
        if (useProgram) useProgram((GLuint)program);
        glPopClientAttrib();
        glPopAttrib();
        while (glGetError() != GL_NO_ERROR) {}
        ff::MarkDrawn();
    }

    BOOL WINAPI HookSwap(HDC hdc)
    {
        if (g_depth == 0)
        {
            Draw(hdc);
            ff::OnFrame(FF_API_OPENGL);
        }
        g_depth++;
        BOOL r = g_origSwap(hdc);
        g_depth--;
        return r;
    }
}

namespace
{
    SwapFn g_origGdiSwap = nullptr;    // gdi32!SwapBuffers deviata
    SwapFn g_importSwap = nullptr;     // gdi32!SwapBuffers vera, per l'aggancio sulla tabella di importazione

    BOOL WINAPI HookGdiSwap(HDC hdc)
    {
        if (g_depth == 0 && wglGetCurrentContext())
        {
            Draw(hdc);
            ff::OnFrame(FF_API_OPENGL);
        }
        g_depth++;
        BOOL r = g_origGdiSwap(hdc);
        g_depth--;
        return r;
    }

    BOOL WINAPI ImportSwap(HDC hdc)
    {
        if (g_depth == 0 && wglGetCurrentContext())
        {
            Draw(hdc);
            ff::OnFrame(FF_API_OPENGL);
        }
        g_depth++;
        BOOL r = g_importSwap(hdc);
        g_depth--;
        return r;
    }

    // Sostituisce, nella tabella di importazione di un modulo, il puntatore a gdi32!SwapBuffers.
    // A differenza di una deviazione sul codice, questo non viene cancellato dagli altri overlay.
    bool PatchImport(HMODULE module)
    {
        auto* base = (uint8_t*)module;
        auto* dos = (IMAGE_DOS_HEADER*)base;
        if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
        auto* nt = (IMAGE_NT_HEADERS*)(base + dos->e_lfanew);
        auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
        if (!dir.VirtualAddress) return false;

        bool patched = false;
        for (auto* imp = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); imp->Name; imp++)
        {
            if (_stricmp((const char*)(base + imp->Name), "gdi32.dll") != 0) continue;
            for (auto* thunk = (IMAGE_THUNK_DATA*)(base + imp->FirstThunk); thunk->u1.Function; thunk++)
            {
                if ((void*)thunk->u1.Function != (void*)g_importSwap) continue;
                DWORD old;
                if (!VirtualProtect(&thunk->u1.Function, sizeof(void*), PAGE_READWRITE, &old)) continue;
                thunk->u1.Function = (ULONG_PTR)ImportSwap;
                VirtualProtect(&thunk->u1.Function, sizeof(void*), old, &old);
                patched = true;
            }
        }
        return patched;
    }
}

bool ff::HookOpenGL()
{
    HMODULE gl = GetModuleHandleW(L"opengl32.dll");
    HMODULE gdi = GetModuleHandleW(L"gdi32.dll");

    // Tre vie, dalla più robusta alla più generale; un contatore evita di contare due volte.
    bool any = false;
    g_importSwap = gdi ? (SwapFn)GetProcAddress(gdi, "SwapBuffers") : nullptr;
    if (g_importSwap && PatchImport(GetModuleHandleW(nullptr)))
    {
        Log("OpenGL: agganciata l'importazione di SwapBuffers nel gioco");
        any = true;
    }
    any |= InlineHook("OpenGL gdi32 SwapBuffers", (void*)g_importSwap, (void*)HookGdiSwap, (void**)&g_origGdiSwap);
    any |= InlineHook("OpenGL wglSwapBuffers", gl ? (void*)GetProcAddress(gl, "wglSwapBuffers") : nullptr,
        (void*)HookSwap, (void**)&g_origSwap);
    return any;
}
