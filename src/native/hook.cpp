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

    SRWLOCK g_overlayLock = SRWLOCK_INIT;

    namespace
    {
        // Timer dell'attesa: uno per thread (due thread che presentano non si rovinano l'attesa a vicenda).
        // Creato alla prima attesa del thread; ad alta risoluzione se il sistema lo offre.
        thread_local HANDLE t_timer = nullptr;
        thread_local bool t_timerHigh = false, t_timerTried = false;
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

        // Attesa precisa: timer fino a poco prima del traguardo (~0,5 ms con il timer ad alta risoluzione,
        // ~1 ms con quello normale, meno preciso), poi attesa attiva.
        void WaitUntil(double target)
        {
            if (!t_timerTried)
            {
                t_timerTried = true;
                // Timer ad alta risoluzione (Windows 10 1803+); se manca si ripiega su quello normale.
                t_timer = CreateWaitableTimerExW(nullptr, nullptr, 0x00000002 /*HIGH_RESOLUTION*/, TIMER_ALL_ACCESS);
                t_timerHigh = t_timer != nullptr;
                if (!t_timer) t_timer = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
            }
            const double margin = t_timerHigh ? 0.5 : 1.0;
            for (;;)
            {
                double remainingMs = (target - (double)Qpc()) * 1000.0 / (double)g_freq;
                if (remainingMs <= 0) return;
                if (remainingMs > margin + 0.5 && t_timer)
                {
                    LARGE_INTEGER due;
                    due.QuadPart = -(LONGLONG)((remainingMs - margin) * 10000.0); // unità da 100 ns, relativo
                    if (SetWaitableTimerEx(t_timer, &due, 0, nullptr, nullptr, nullptr, 0))
                        WaitForSingleObject(t_timer, 100);
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

        // Preparazione comune (una volta sola, anche se la chiedono insieme il thread dell'iniezione e il
        // layer Vulkan): frequenza del contatore e memoria condivisa.
        INIT_ONCE g_coreOnce = INIT_ONCE_STATIC_INIT;
        bool g_coreOk = false;

        BOOL CALLBACK InitCoreOnce(PINIT_ONCE, PVOID, PVOID*)
        {
            LARGE_INTEGER f;
            QueryPerformanceFrequency(&f);
            g_freq = f.QuadPart;
            g_coreOk = CreateShared();
            return TRUE;
        }

        // Tiene la DLL caricata fino alla fine del processo: con agganci e thread attivi non deve
        // mai essere scaricata (né da FreeLibrary del loader Vulkan né da altri).
        void PinSelf()
        {
            HMODULE pinned = nullptr;
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
                               (LPCWSTR)&PinSelf, &pinned);
        }

        DWORD WINAPI InitThread(LPVOID param)
        {
            HMODULE self = (HMODULE)param;
            if (!InitCore()) { Log("memoria condivisa non creata: mi fermo"); return 0; }
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
                if (d3d12) D3D12Maintain();
                if (dxgi) DxgiMaintain();
            }
            return 0;
        }
    }

    bool InitCore()
    {
        InitOnceExecuteOnce(&g_coreOnce, InitCoreOnce, nullptr, nullptr);
        return g_coreOk;
    }

    bool StartLayerMode()
    {
        PinSelf();   // da qui in poi esistono stato e agganci: la DLL non deve più essere scaricata
        bool ok = InitCore();
        static volatile LONG logged = 0;
        if (InterlockedExchange(&logged, 1) == 0)
            Log(ok ? "DLL caricata come layer Vulkan" : "layer Vulkan: memoria condivisa non creata");
        return ok;
    }

    // Un gioco può passare da due API insieme (es. Vulkan o OpenGL presentati dal driver tramite DXGI:
    // succede con gli swapchain Vulkan HDR delle schede NVIDIA): conta solo la prima che si è fatta viva,
    // le altre vengono ignorate finché quella resta attiva.
    static uint32_t g_frameOwner = 0;
    static int64_t g_frameOwnerLast = 0;

    volatile LONG g_diagNotPrimary = 0, g_diagRetry = 0;

    static const char* ApiName(uint32_t api)
    {
        switch (api)
        {
        case FF_API_DXGI: return "DXGI";
        case FF_API_D3D12: return "DirectX 12";
        case FF_API_D3D9: return "DirectX 9";
        case FF_API_OPENGL: return "OpenGL";
        case FF_API_VULKAN: return "Vulkan";
        case FF_API_D3D11: return "DirectX 11";
        case FF_API_D3D10: return "DirectX 10";
        default: return "nessuna";
        }
    }

    bool OwnsFrames(uint32_t api)
    {
        return g_frameOwner == 0 || g_frameOwner == api || Qpc() - g_frameOwnerLast >= g_freq / 2;
    }

    void OnFrame(uint32_t api)
    {
        FFShared* s = g_shared;
        if (!s) return;

        int64_t nowQpc = Qpc();
        // Diario (sempre con un tetto, mai una riga per frame): chi guida i fotogrammi, quali API vengono
        // ignorate, lo stato del limitatore e, una volta al minuto, un controllo dei conti.
        static int ownerLogs = 0;
        static uint32_t ignoredLogged = 0, dIgnored = 0;
        if (g_frameOwner != api && g_frameOwner != 0 && nowQpc - g_frameOwnerLast < g_freq / 2)
        {
            dIgnored++;
            if (!(ignoredLogged & (1u << (api & 31))) && ownerLogs < 24)
            {
                ignoredLogged |= 1u << (api & 31);
                ownerLogs++;
                Log("Fotogrammi: %s presenta ma viene ignorata (guida %s)", ApiName(api), ApiName(g_frameOwner));
            }
            return;
        }
        if (g_frameOwner != api && g_frameOwner != 0)
        {
            ignoredLogged = 0;
            if (ownerLogs < 24)
            {
                ownerLogs++;
                Log("Fotogrammi: ora li guida %s (prima %s, ferma da %lld ms)", ApiName(api), ApiName(g_frameOwner),
                    (long long)((nowQpc - g_frameOwnerLast) * 1000 / g_freq));
            }
        }
        g_frameOwner = api;
        g_frameOwnerLast = nowQpc;

        // --- frame generation ---
        // Con DLSS FG / FSR FG il gioco consegna il fotogramma vero e quelli generati "a raffica":
        // il secondo arriva una frazione di millisecondo dopo che il primo è stato consegnato, poi è
        // la scheda video a distribuirli sullo schermo. Lo si riconosce dal tempo tra la fine di una
        // consegna e l'inizio della successiva (che le nostre attese non alterano).
        //
        // La vera frame generation ha un ritmo regolare (x2: L C L C..., x3: L C C L C C...): due intervalli
        // lunghi di fila (L L) quasi non capitano, e ogni gruppo di corti è lungo "raffica - 1". Un gioco
        // leggero il cui Present si blocca a caso (limitatore del driver, vsync) produce invece corti sparsi
        // e molte coppie L L: lì la modalità raffica NON va accesa (frenerebbe i frame a coppie: scatti).
        // Per accenderla servono 16 frame di fila con il ritmo giusto; si spegne subito se le coppie L L
        // diventano tante, oppure dopo 32 frame di fila senza il ritmo giusto. Tutto O(1), niente allocazioni.
        static int64_t lastExit = 0;
        static int g_freeStreak = 1000;   // fotogrammi di fila che il limitatore non ha frenato
        static double longEma = 0;
        static bool ring[64] = {};       // intervallo corto?
        static bool llRing[64] = {};     // intervallo lungo subito dopo un altro lungo?
        static int ringPos = 0, ringCount = 0, shortCount = 0, llCount = 0;
        static bool prevShort = true;    // il primo lungo non fa coppia
        static uint8_t runRing[16] = {}; // lunghezze degli ultimi gruppi di corti (4 = 4 o più)
        static int runPos = 0, runFill = 0, runOfLen[5] = {}, curRun = 0;
        static bool fgOn = false;
        static int fgBurst = 2, enterStreak = 0, failStreak = 0;
        // "Lungo" e "corto" si decidono rispetto all'intervallo lungo solito (longEma), che si aggiorna solo
        // con gli intervalli lunghi. Senza due protezioni basta UNO scatto del gioco (caricamento, shader:
        // 100-900 ms) per gonfiarlo, far sembrare corti tutti gli intervalli dopo e, non arrivando più
        // intervalli lunghi a correggerlo, restare sbagliato per sempre: la frame generation non veniva più
        // riconosciuta fino al riavvio del gioco.
        //  1) uno scatto isolato (oltre 4 volte il solito) si mette da parte; solo al terzo di fila è un
        //     rallentamento vero e diventa il nuovo riferimento;
        //  2) più di 5 corti di fila non esistono (x6 ne ha 5): vuol dire che il riferimento è troppo alto,
        //     e lo si riporta al più lungo di quel gruppo.
        static int outliers = 0, fgLogs = 0;
        static double runMax = 0;
        bool isShort = false;
        // Il ritmo si giudica solo sui fotogrammi che il limitatore non ha appena frenato. Quando e' lui a
        // tenere fermo il gioco, i fotogrammi dopo sono gia' pronti e arrivano tutti "subito": il tempo tra una
        // consegna e l'altra non dice piu' niente della frame generation, e a giudicarlo lo stesso la modalita'
        // raffica si accendeva e spegneva ogni secondo (FPS che ballano attorno al limite). Mentre frena, lo
        // stato resta quello deciso prima; un gioco frenato dall'inizio resta al passo regolare, un
        // fotogramma ogni intervallo, che e' comunque il limite giusto.
        bool clean = g_freeStreak >= 8;
        if (lastExit != 0 && !clean)
        {
            double gap = (double)(nowQpc - lastExit);
            isShort = longEma > 0 && gap < 0.35 * longEma;   // serve solo a chi frena, non alle statistiche
            prevShort = true;
            curRun = 0;
            runMax = 0;
            enterStreak = 0;
        }
        else if (lastExit != 0)
        {
            double gap = (double)(nowQpc - lastExit);
            if (gap < (double)g_freq && longEma > 0 && gap > 4.0 * longEma && outliers < 2)
            {
                outliers++;
                prevShort = true;   // come dopo una pausa: niente coppia L L, gruppo di corti da capo
                curRun = 0;
                runMax = 0;
            }
            else if (gap < (double)g_freq)   // le pause lunghe (caricamenti) non contano
            {
                isShort = longEma > 0 && gap < 0.35 * longEma;
                if (!isShort)
                {
                    longEma = outliers >= 2 ? gap : gap > longEma ? (longEma + gap) / 2 : longEma * 0.9 + gap * 0.1;
                    outliers = 0;
                    runMax = 0;
                }
                else
                {
                    if (gap > runMax) runMax = gap;
                    if (curRun >= 5)
                    {
                        // Tetto a parte e basso: in un gioco senza frame generation capita di continuo e
                        // toglierebbe il posto alle righe "riconosciuta" / "persa".
                        static int snapLogs = 0;
                        if (snapLogs < 4) { snapLogs++; Log("Limitatore: ritmo riallineato (intervallo lungo %.2f -> %.2f ms)", longEma * 1000.0 / g_freq, runMax * 1000.0 / g_freq); }
                        longEma = runMax;
                        runMax = 0;
                        curRun = -1;   // il gruppo riparte da zero (qui sotto sale di uno)
                    }
                }
                bool ll = !isShort && !prevShort;
                if (ringCount == 64)
                {
                    if (ring[ringPos]) shortCount--;
                    if (llRing[ringPos]) llCount--;
                }
                ring[ringPos] = isShort;
                llRing[ringPos] = ll;
                if (isShort) shortCount++;
                if (ll) llCount++;
                ringPos = (ringPos + 1) % 64;
                if (ringCount < 64) ringCount++;

                if (isShort) curRun++;
                else if (curRun > 0)
                {
                    // Fine di un gruppo di corti: se ne ricorda la lunghezza.
                    uint8_t len = (uint8_t)(curRun < 4 ? curRun : 4);
                    if (runFill == 16) runOfLen[runRing[runPos]]--;
                    runRing[runPos] = len;
                    runOfLen[len]++;
                    runPos = (runPos + 1) % 16;
                    if (runFill < 16) runFill++;
                    curRun = 0;
                }
                prevShort = isShort;
            }
            else
            {
                prevShort = true;   // dopo una pausa si riparte senza coppia L L
                curRun = 0;
                runMax = 0;
            }

            bool rhythm = false;
            int candidate = 2;
            if (ringCount >= 32)
            {
                double share = (double)shortCount / ringCount;   // x2 = 0,5  x3 = 0,67  x4 = 0,75
                candidate = share < 0.58 ? 2 : share < 0.71 ? 3 : 4;
                int wanted = candidate - 1;                      // lunghezza attesa dei gruppi di corti
                rhythm = share >= 0.3 && share <= 0.85 && llCount <= 3 &&
                         runFill >= 8 && runOfLen[wanted] * 4 >= runFill * 3;
            }
            if (!fgOn)
            {
                enterStreak = rhythm ? enterStreak + 1 : 0;
                if (enterStreak >= 16)
                {
                    fgOn = true; fgBurst = candidate; failStreak = 0;
                    if (fgLogs < 24) { fgLogs++; Log("Limitatore: frame generation x%d riconosciuta", candidate); }
                }
            }
            else if (rhythm)
            {
                failStreak = 0;
                fgBurst = candidate;
            }
            else if (llCount > 8 || ++failStreak >= 32)
            {
                fgOn = false;
                enterStreak = 0;
                if (fgLogs < 24)
                {
                    fgLogs++;
                    Log("Limitatore: frame generation persa (corti %d su %d, coppie lunghe %d, intervallo lungo %.2f ms)",
                        shortCount, ringCount, llCount, longEma * 1000.0 / g_freq);
                }
            }
        }
        bool fgMode = fgOn;
        int burst = fgOn ? fgBurst : 1;

        // --- limitatore ---
        double fps = 0;
        if (s->limiterEnabled && AppAlive(s)) fps = s->targetFps;

        // Diario: ogni cambio di stato del limitatore (acceso con quale limite, spento dall'app, app muta).
        static int limState = 0, limLogs = 0;
        static double limFps = 0;
        static uint32_t dFrames = 0, dWaited = 0, dLate = 0, dPassed = 0;
        {
            int state = fps >= 1.0 ? 1 : !s->limiterEnabled ? 2 : !AppAlive(s) ? 3 : 4;
            if ((state != limState || (state == 1 && fps != limFps)) && limLogs < 40)
            {
                limLogs++;
                if (state == 1) Log("Limitatore: attivo a %.0f fps", fps);
                else if (state == 2) Log("Limitatore: spento dall'app");
                else if (state == 3) Log("Limitatore: sospeso, l'app non si fa sentire da %llu ms", (unsigned long long)(GetTickCount64() - s->heartbeatMs));
                else Log("Limitatore: limite non valido (%.2f)", s->targetFps);
            }
            limState = state;
            limFps = fps;
        }
        dFrames++;

        if (fps >= 1.0)
        {
            // Con la frame generation si frena la raffica intera, non ogni fotogramma: quelli generati
            // passano subito, altrimenti ci si metterebbe in mezzo al lavoro della scheda video.
            // Il limite resta quello dei fotogrammi che si vedono (100 fps = 50 raffiche da due).
            //
            // Il conto pero' si tiene su OGNI fotogramma (ognuno sposta l'orario di un intervallo), non sulle
            // raffiche: se il ritmo non e' quello creduto (piu' generati del previsto, per esempio con il menu
            // di un involucro aperto sopra la frame generation) il limite tiene lo stesso. Prima i "corti"
            // passavano senza essere contati e bastava un corto in piu' ogni tanto per superare il limite.
            // Un corto aspetta solo se e' in anticipo di piu' di una raffica intera.
            double per = (double)g_freq / fps;
            double interval = per * burst;
            double now = (double)Qpc();
            bool passing = fgMode && isShort;
            // Primo frame, oppure frame in ritardo (oltre il 10% dell'intervallo): si riparte da qui,
            // senza "recuperare" con frame attaccati uno all'altro.
            if (g_next == 0 || now > g_next + 0.1 * interval)
            {
                g_next = now;
                if (passing) dPassed++; else dLate++;
            }
            else if (!passing && now < g_next)
            {
                WaitUntil(g_next);
                dWaited++;
                g_freeStreak = 0;
            }
            else if (passing && now < g_next - interval)
            {
                WaitUntil(g_next - interval);
                dWaited++;
                g_freeStreak = 0;
            }
            else if (passing)
            {
                dPassed++;
            }
            g_next += per;
        }
        else
        {
            g_next = 0;
        }
        if (g_freeStreak < 1000) g_freeStreak++;   // dopo un'attesa riparte da 1: servono 8 fotogrammi liberi di fila

        // --- misura ---
        uint64_t idx = s->writeIndex;
        int64_t stamp = Qpc();
        s->ring[idx % FF_RING] = stamp;
        GraphOnPresent(stamp, g_freq);   // lo stesso istante che legge l'app: grafico e numeri dicono la stessa cosa
        s->api = api;
        MemoryBarrier();
        s->writeIndex = idx + 1;

        // Diario, una volta al minuto: i conti dell'ultimo minuto. La riga si scrive se qualcosa non torna
        // (più fotogrammi del limite, Present passati fuori dal limitatore, limitatore non acceso), altrimenti
        // una ogni dieci minuti. Così, se in un gioco il limite "non tiene", si vede da che parte passano i frame.
        static int64_t statStart = 0;
        static int statLogs = 0, statQuiet = 0;
        if (statStart == 0) statStart = stamp;
        else if (stamp - statStart >= 60 * g_freq)
        {
            double secs = (double)(stamp - statStart) / (double)g_freq;
            double measured = dFrames / secs;
            LONG notPrimary = InterlockedExchange(&g_diagNotPrimary, 0), retry = InterlockedExchange(&g_diagRetry, 0);
            bool over = fps >= 1.0 && measured > fps * 1.05;
            bool odd = over || fps < 1.0 || dIgnored > 0 || notPrimary > 0 || retry > 0;
            if ((odd || ++statQuiet >= 10) && statLogs < 120)
            {
                statLogs++;
                statQuiet = 0;
                Log("Stato%s: %s, limite %.0f, %.1f fps in %.0f s (attesi %u, in ritardo %u, generati %u, raffica x%d), "
                    "fuori dal limitatore: altre API %u, altri swapchain %ld, tentativi ripetuti %ld",
                    over ? " (PIU' DEL LIMITE)" : "", ApiName(api), fps, measured, secs, dWaited, dLate, dPassed, burst,
                    dIgnored, (long)notPrimary, (long)retry);
            }
            statStart = stamp;
            dFrames = dWaited = dLate = dPassed = dIgnored = 0;
        }
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
        uint32_t corner = s->ovCorner, margin = s->ovMargin;   // scritti dall'app: si controllano
        out.corner = corner <= 4 ? corner : 0;
        out.margin = margin <= 4096 ? margin : 0;
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
            bool reported;      // già scritto nel diario che un altro programma l'ha sostituito
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
        Inline h = { name, (uint8_t*)target, hook, original, {}, false };
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
            // Nessuna "riparazione": MH_RemoveHook rimetterebbe il codice originale (cancellando il salto
            // dell'altro programma) e libererebbe il nostro trampolino mentre un thread può starci dentro.
            // Gli agganci che contano (tabella del device, importazioni) non passano da qui.
            if (h.reported || memcmp(h.bytes, h.target, 5) == 0) continue;
            h.reported = true;
            Log("%s: aggancio sostituito da un altro programma, lo lascio com'è", h.name);
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
        // Due modi di arrivare qui. Iniezione (ffinject): l'iniettore crea prima un segnale con il PID del
        // gioco, e allora si parte come sempre. Altrimenti è il loader Vulkan che carica il layer in un
        // programma qualunque: qui non si avvia nulla (il loader può scaricare la DLL subito dopo, se il
        // layer viene rifiutato); il layer si prepara da sé in vkCreateInstance, solo se accettato.
        wchar_t name[64];
        swprintf_s(name, FF_INJECT_MARKER_PREFIX L"%lu", GetCurrentProcessId());
        HANDLE marker = OpenEventW(SYNCHRONIZE, FALSE, name);
        if (marker)
        {
            CloseHandle(marker);
            ff::PinSelf();
            HANDLE t = CreateThread(nullptr, 0, ff::InitThread, module, 0, nullptr);
            if (t) CloseHandle(t);
        }
    }
    return TRUE;
}
