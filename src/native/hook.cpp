// FrameGripHook: DLL caricata dentro il gioco.
// Questo file contiene il cuore comune: memoria condivisa, limitatore, immagine dell'overlay
// e il thread che aggancia le API grafiche man mano che il gioco le carica.
#include "common.h"
#include <vector>
#include <cwchar>
#include <cstring>
#include <cstdio>
#include <cstdarg>
#include "third_party/minhook/include/MinHook.h"

namespace ff
{
    FFShared* g_shared = nullptr;

    namespace
    {
        HANDLE g_timer = nullptr;
        int64_t g_freq = 1;
        double g_next = 0;      // istante previsto del prossimo Present, in tick QPC

        std::vector<uint8_t> g_ovLocal;
        uint32_t g_ovSeq = 0xFFFFFFFF, g_ovW = 0, g_ovH = 0, g_ovSerial = 0;
        bool g_ovForceFull = true;
        // Righe cambiate nelle ultime copie (per numero di serie): chi carica la texture con qualche
        // frame di ritardo ricarica l'unione delle righe cambiate nel frattempo, non l'immagine intera.
        struct DirtyRange { uint32_t serial, y0, y1; };
        const uint32_t kDirtyHistory = 32;
        DirtyRange g_dirty[kDirtyHistory] = {};

        inline int64_t Qpc()
        {
            LARGE_INTEGER t;
            QueryPerformanceCounter(&t);
            return t.QuadPart;
        }

        // Un nuovo contenuto della copia locale: righe [y0, y1) cambiate.
        void PushDirty(uint32_t y0, uint32_t y1)
        {
            g_ovSerial++;
            DirtyRange& d = g_dirty[g_ovSerial % kDirtyHistory];
            d.serial = g_ovSerial;
            d.y0 = y0;
            d.y1 = y1;
        }

        inline bool AppAlive(const FFShared* s)
        {
            return GetTickCount64() - s->heartbeatMs < 3000;
        }

        // Attesa precisa: timer ad alta risoluzione fino a ~1 ms dal traguardo, poi attesa attiva.
        void WaitUntil(double target)
        {
            for (;;)
            {
                double remainingMs = (target - (double)Qpc()) * 1000.0 / (double)g_freq;
                if (remainingMs <= 0) return;
                if (remainingMs > 1.5 && g_timer)
                {
                    LARGE_INTEGER due;
                    due.QuadPart = -(LONGLONG)((remainingMs - 1.0) * 10000.0); // unità da 100 ns, relativo
                    if (SetWaitableTimerEx(g_timer, &due, 0, nullptr, nullptr, nullptr, 0))
                        WaitForSingleObject(g_timer, 100);
                    else
                        Sleep(1);
                }
                else
                {
                    YieldProcessor();
                }
            }
        }

        bool CreateShared()
        {
            wchar_t name[64];
            swprintf_s(name, FF_SHARED_PREFIX L"%lu", GetCurrentProcessId());
            HANDLE map = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(FFShared), name);
            if (!map) return false;
            bool existed = GetLastError() == ERROR_ALREADY_EXISTS;
            auto* s = (FFShared*)MapViewOfFile(map, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(FFShared));
            if (!s) return false;
            if (!existed)
            {
                s->version = FF_SHARED_VERSION;
                s->qpcFrequency = g_freq;
                s->dllCaps = FF_CAP_GRAPH | FF_CAP_HDR;
            }
            g_shared = s;
            return true;
        }

        DWORD WINAPI InitThread(LPVOID param)
        {
            HMODULE self = (HMODULE)param;
            LARGE_INTEGER f;
            QueryPerformanceFrequency(&f);
            g_freq = f.QuadPart;

            // Timer ad alta risoluzione (Windows 10 1803+); se manca si ripiega su quello normale.
            g_timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /*HIGH_RESOLUTION*/, TIMER_ALL_ACCESS);
            if (!g_timer) g_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);

            if (!CreateShared()) { Log("memoria condivisa non creata: mi fermo"); return 0; }
            Log("DLL caricata nel processo");

            // Il gioco può caricare la sua API grafica anche dopo l'iniezione:
            // si controlla ogni secondo e si aggancia ciò che compare.
            MH_Initialize();
            bool dxgi = false, d3d9 = false, gl = false, d3d12 = false;
            int dxgiTries = 0, d3d9Tries = 0, glTries = 0, d3d12Tries = 0;
            for (;;)
            {
                if (!d3d12 && d3d12Tries < 3 && GetModuleHandleW(L"d3d12.dll"))
                {
                    Log("DirectX 12: provo ad agganciare");
                    d3d12 = HookD3D12();
                    d3d12Tries++;
                    Log("DirectX 12 (coda di comandi): %s", d3d12 ? "agganciata" : "aggancio fallito");
                }
                if (!gl && glTries < 3 && GetModuleHandleW(L"opengl32.dll"))
                {
                    gl = HookOpenGL();
                    glTries++;
                    Log("OpenGL: %s", gl ? "agganciato" : "aggancio fallito");
                }
                if (!dxgi && dxgiTries < 5 && GetModuleHandleW(L"dxgi.dll") &&
                    (GetModuleHandleW(L"d3d11.dll") || GetModuleHandleW(L"d3d12.dll") || GetModuleHandleW(L"d3d10.dll") || GetModuleHandleW(L"d3d10_1.dll")))
                {
                    Log("DXGI: provo ad agganciare");
                    dxgi = HookDxgi(self);
                    dxgiTries++;
                    Log("DXGI (DirectX 10/11/12): %s", dxgi ? "agganciato" : "aggancio fallito");
                }
                if (!d3d9 && d3d9Tries < 5 && GetModuleHandleW(L"d3d9.dll"))
                {
                    d3d9 = HookD3D9(self);
                    d3d9Tries++;
                    Log("Direct3D 9: %s", d3d9 ? "agganciato" : "aggancio fallito");
                }
                Sleep(1000);
                CheckInlineHooks();
            }
            return 0;
        }
    }

    // Un gioco può passare da due API insieme (es. Vulkan o OpenGL presentati dal driver tramite DXGI:
    // succede con gli swapchain Vulkan HDR delle schede NVIDIA): conta solo la prima che si è fatta viva,
    // le altre vengono ignorate finché quella resta attiva.
    static uint32_t g_frameOwner = 0;
    static int64_t g_frameOwnerLast = 0;

    bool OwnsFrames(uint32_t api)
    {
        return g_frameOwner == 0 || g_frameOwner == api || Qpc() - g_frameOwnerLast >= g_freq / 2;
    }

    void OnFrame(uint32_t api)
    {
        FFShared* s = g_shared;
        if (!s) return;

        int64_t nowQpc = Qpc();
        if (g_frameOwner != api && g_frameOwner != 0 && nowQpc - g_frameOwnerLast < g_freq / 2) return;
        g_frameOwner = api;
        g_frameOwnerLast = nowQpc;

        // --- frame generation ---
        // Con DLSS FG / FSR FG il gioco consegna il fotogramma vero e quelli generati "a raffica":
        // il secondo arriva una frazione di millisecondo dopo che il primo è stato consegnato, poi è
        // la scheda video a distribuirli sullo schermo. Lo si riconosce dal tempo tra la fine di una
        // consegna e l'inizio della successiva (che le nostre attese non alterano).
        static int64_t lastExit = 0;
        static double longEma = 0;
        static bool ring[64] = {};
        static int ringPos = 0, ringCount = 0, shortCount = 0;
        bool isShort = false, fgMode = false;
        int burst = 1;
        if (lastExit != 0)
        {
            double gap = (double)(nowQpc - lastExit);
            if (gap < (double)g_freq)   // le pause lunghe (caricamenti) non contano
            {
                isShort = longEma > 0 && gap < 0.35 * longEma;
                if (!isShort) longEma = gap > longEma ? (longEma + gap) / 2 : longEma * 0.9 + gap * 0.1;
                if (ringCount == 64 && ring[ringPos]) shortCount--;
                ring[ringPos] = isShort;
                if (isShort) shortCount++;
                ringPos = (ringPos + 1) % 64;
                if (ringCount < 64) ringCount++;
            }
            if (ringCount >= 32)
            {
                double share = (double)shortCount / ringCount;   // x2 = 0,5  x3 = 0,67  x4 = 0,75
                fgMode = share >= 0.3 && share <= 0.85;
                if (fgMode) burst = share < 0.58 ? 2 : share < 0.71 ? 3 : 4;
            }
        }

        // --- limitatore ---
        double fps = 0;
        if (s->limiterEnabled && AppAlive(s)) fps = s->targetFps;

        if (fps >= 1.0)
        {
            // Con la frame generation si frena la raffica intera, non ogni fotogramma: quelli generati
            // passano subito, altrimenti ci si metterebbe in mezzo al lavoro della scheda video.
            // Il limite resta quello dei fotogrammi che si vedono (100 fps = 50 raffiche da due).
            if (!(fgMode && isShort))
            {
                double interval = (double)g_freq / fps * burst;
                double now = (double)Qpc();
                if (g_next == 0 || now - g_next > interval)
                    g_next = now;               // primo frame, oppure siamo in ritardo: si riparte da qui
                else if (now < g_next)
                    WaitUntil(g_next);
                g_next += interval;
            }
        }
        else
        {
            g_next = 0;
        }

        // --- misura ---
        uint64_t idx = s->writeIndex;
        int64_t stamp = Qpc();
        s->ring[idx % FF_RING] = stamp;
        GraphOnPresent(stamp, g_freq);   // lo stesso istante che legge l'app: grafico e numeri dicono la stessa cosa
        s->api = api;
        MemoryBarrier();
        s->writeIndex = idx + 1;
        lastExit = Qpc();
    }

    bool GetOverlay(Overlay& out)
    {
        FFShared* s = g_shared;
        if (!s || !s->overlayVisible || !AppAlive(s)) return false;

        // Si guarda a ogni Present: un'immagine nuova si vede già al frame successivo alla scrittura.
        uint32_t seq = s->ovSeq;
        if (!(seq & 1) && seq != g_ovSeq)
        {
            MemoryBarrier();
            uint32_t w = s->ovWidth, h = s->ovHeight;
            if (w && h && w <= FF_OV_MAX_W && h <= FF_OV_MAX_H)
            {
                // Basta copiare le sole righe cambiate (la striscia del grafico, fino a 60 volte al secondo)
                // se dall'ultima copia riuscita non c'è stata un'immagine intera nuova e la misura è la stessa.
                bool full = g_ovForceFull || w != g_ovW || h != g_ovH || (int32_t)(s->ovFullSeq - g_ovSeq) > 0;
                uint32_t y0 = 0, rows = h;
                if (!full)
                {
                    y0 = s->ovDirtyY;
                    rows = s->ovDirtyRows;
                    if (y0 >= h || rows > h - y0) { full = true; y0 = 0; rows = h; }
                }
                size_t bytes = (size_t)w * h * 4;
                if (g_ovLocal.size() < bytes) g_ovLocal.resize(bytes);   // solo quando l'immagine cresce
                size_t row = (size_t)w * 4;
                if (rows) memcpy(g_ovLocal.data() + y0 * row, s->ovPixels + y0 * row, rows * row);
                FFShared::Graph graph = s->graph;   // scritto dall'app con l'immagine intera, sotto lo stesso ovSeq
                MemoryBarrier();
                if (s->ovSeq == seq)   // l'app non ha scritto durante la copia: l'immagine è integra
                {
                    g_ovW = w;
                    g_ovH = h;
                    g_ovSeq = seq;
                    g_ovForceFull = false;
                    if (full) GraphTakeParams(graph);
                    GraphImageCopied(g_ovLocal.data(), w, h, y0, rows, full);
                    if (rows) PushDirty(y0, y0 + rows);
                }
                else
                {
                    // Copia a metà: si continua a mostrare l'immagine precedente (la texture non cambia,
                    // perché il numero di serie resta quello) e al prossimo Present si ricopia tutto.
                    g_ovForceFull = true;
                    g_ovSeq = 0xFFFFFFFF;
                }
            }
        }
        if (!g_ovW || !g_ovH) return false;

        // La linea del grafico, se l'app l'ha chiesta alla DLL: solo le righe del riquadro cambiano.
        uint32_t gy0, gy1;
        if (GraphDraw(g_ovLocal.data(), g_ovW, g_ovH, gy0, gy1)) PushDirty(gy0, gy1);

        out.width = g_ovW;
        out.height = g_ovH;
        out.corner = s->ovCorner;
        out.margin = s->ovMargin;
        out.pixels = g_ovLocal.data();
        out.serial = g_ovSerial;
        float nits = s->hdrWhiteNits;   // 4 byte allineati: lettura atomica, nessun blocco
        out.whiteNits = nits >= 40.0f && nits <= 1000.0f ? nits : 200.0f;   // 0 (app vecchia) o NaN: 200
        return true;
    }

    void RowsToUpload(const Overlay& ov, uint32_t texSerial, bool fresh, uint32_t& y0, uint32_t& y1)
    {
        y0 = 0;
        y1 = ov.height;
        uint32_t behind = ov.serial - texSerial;
        if (fresh || behind == 0 || behind >= kDirtyHistory) return;   // texture nuova o troppo indietro: tutta
        uint32_t lo = ov.height, hi = 0;
        for (uint32_t s = texSerial + 1; s != ov.serial + 1; s++)
        {
            const DirtyRange& d = g_dirty[s % kDirtyHistory];
            if (d.serial != s) return;   // storia sovrascritta: tutta
            if (d.y0 < lo) lo = d.y0;
            if (d.y1 > hi) hi = d.y1;
        }
        if (hi > ov.height) hi = ov.height;
        if (lo >= hi) return;
        y0 = lo;
        y1 = hi;
    }

    ColorConsts MakeColorConsts(uint32_t mode, float whiteNits)
    {
        // scRGB: 1.0 = 80 nit, quindi il bianco a 200 nit vale 2.5. HDR10: lo shader lavora direttamente in nit.
        float scale = mode == kColorScRgb ? whiteNits / 80.0f : mode == kColorHdr10 ? whiteNits : 1.0f;
        return { (float)mode, scale, 0.0f, 0.0f };
    }

    const char* ColorModeName(uint32_t mode)
    {
        switch (mode)
        {
        case kColorSdrSrgb: return "SDR (vista sRGB)";
        case kColorScRgb: return "HDR scRGB";
        case kColorHdr10: return "HDR10 (PQ)";
        default: return "SDR";
        }
    }

    void Place(const Overlay& o, uint32_t targetW, uint32_t targetH, int& x, int& y)
    {
        bool right = o.corner == 1 || o.corner == 3;
        bool bottom = o.corner == 2 || o.corner == 3;
        x = right ? (int)targetW - (int)o.width - (int)o.margin : (int)o.margin;
        if (o.corner == 4) x = ((int)targetW - (int)o.width) / 2;   // in alto al centro
        y = bottom ? (int)targetH - (int)o.height - (int)o.margin : (int)o.margin;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
    }

    void MarkDrawn()
    {
        if (g_shared) g_shared->lastDrawMs = GetTickCount64();
    }

    void Log(const char* format, ...)
    {
        wchar_t dir[MAX_PATH], path[MAX_PATH], exe[MAX_PATH] = L"";
        DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", dir, MAX_PATH);
        if (!n || n >= MAX_PATH - 32) return;
        wcscat_s(dir, L"\\FrameGrip");
        CreateDirectoryW(dir, nullptr);
        swprintf_s(path, L"%s\\hook.log", dir);

        char text[512], line[1024];
        va_list args;
        va_start(args, format);
        vsnprintf(text, sizeof(text), format, args);
        va_end(args);

        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        const wchar_t* name = wcsrchr(exe, L'\\');
        SYSTEMTIME t;
        GetLocalTime(&t);
        int len = snprintf(line, sizeof(line), "%02d:%02d:%02d [%lu %ls] %s\r\n",
            t.wHour, t.wMinute, t.wSecond, GetCurrentProcessId(), name ? name + 1 : exe, text);
        if (len <= 0) return;

        HANDLE f = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) return;
        DWORD written;
        WriteFile(f, line, (DWORD)len, &written, nullptr);
        CloseHandle(f);
    }

    namespace
    {
        struct Inline
        {
            const char* name;
            uint8_t* target;
            void* hook;
            void** original;
            uint8_t bytes[5];   // inizio della funzione subito dopo il nostro aggancio
            int repairs;
        };
        Inline g_inline[16];
        int g_inlineCount = 0;

        bool Apply(Inline& h)
        {
            MH_STATUS st = MH_CreateHook(h.target, h.hook, h.original);
            if (st == MH_OK) st = MH_EnableHook(h.target);
            if (st != MH_OK)
            {
                Log("%s: aggancio fallito a %p (%s)", h.name, h.target, MH_StatusToString(st));
                return false;
            }
            memcpy(h.bytes, h.target, 5);
            return true;
        }
    }

    bool InlineHook(const char* name, void* target, void* hook, void** original)
    {
        if (!target || g_inlineCount >= 16) return false;
        Inline h = { name, (uint8_t*)target, hook, original, {}, 0 };
        if (!Apply(h)) return false;
        g_inline[g_inlineCount++] = h;
        Log("%s agganciata a %p", name, target);
        return true;
    }

    void CheckInlineHooks()
    {
        for (int i = 0; i < g_inlineCount; i++)
        {
            Inline& h = g_inline[i];
            // Pochi tentativi: insistere servirebbe solo a disturbare l'altro overlay. Gli agganci
            // che contano (tabella del device, importazioni) non passano da qui.
            if (memcmp(h.bytes, h.target, 5) == 0 || h.repairs >= 3) continue;
            h.repairs++;
            if (h.repairs <= 2)
                Log("%s: aggancio cancellato da un altro programma, viene rimesso (tentativo %d)", h.name, h.repairs);
            MH_RemoveHook(h.target);    // dimentica la vecchia deviazione senza toccare il codice attuale
            Apply(h);
        }
    }

    bool PatchVtable(void** vtable, int index, void* hook, void** original)
    {
        DWORD old;
        if (!VirtualProtect(&vtable[index], sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return false;
        *original = vtable[index];
        vtable[index] = hook;
        VirtualProtect(&vtable[index], sizeof(void*), old, &old);
        return true;
    }

    HWND CreateProbeWindow(HMODULE self)
    {
        WNDCLASSEXW wc = { sizeof(wc) };
        wc.lpfnWndProc = DefWindowProcW;
        wc.hInstance = self;
        wc.lpszClassName = L"FrameGripProbe";
        RegisterClassExW(&wc);
        return CreateWindowExW(0, wc.lpszClassName, L"", WS_OVERLAPPED, 0, 0, 64, 64, nullptr, nullptr, self, nullptr);
    }

    void DestroyProbeWindow(HWND hwnd, HMODULE self)
    {
        if (hwnd) DestroyWindow(hwnd);
        UnregisterClassW(L"FrameGripProbe", self);
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        HANDLE t = CreateThread(nullptr, 0, ff::InitThread, module, 0, nullptr);
        if (t) CloseHandle(t);
    }
    return TRUE;
}
