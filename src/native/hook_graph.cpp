// Grafico del frametime disegnato dalla DLL, a ogni frame presentato.
// L'app manda solo il riquadro, i colori e la regola della scala (FFShared::graph); i frame la DLL li ha già.
// La linea si disegna nei pixel della copia locale dell'immagine (righe del riquadro) e poi passa per lo
// stesso caricamento a righe di tutto il resto: un solo codice per D3D9/10/11/12, OpenGL e Vulkan.
// Niente allocazioni e niente attese: memoria fissa, poche decine di microsecondi nel caso peggiore.
#include "common.h"
#include <cstring>
#include <cmath>

namespace ff
{
    namespace
    {
        // ---- frame "come si vedono" ----
        // Stessa logica di FrameStats nell'app: con la frame generation il gioco consegna i fotogrammi a
        // raffica (17 ms, 0,4 ms, 17 ms...); quando si riconosce il ritmo, ogni raffica si distribuisce in parti
        // uguali sul tempo fino alla successiva. Così il grafico mostra ciò che descrivono gli FPS, non un pettine.
        const uint32_t kFrames = 8192;            // potenza di 2: a 1000 FPS sono 8 secondi
        double g_ts[kFrames];                     // istante del frame (ms, orologio QPC)
        float g_ft[kFrames];                      // durata (ms)
        volatile uint32_t g_head = 0;             // numero di frame registrati (l'indice è g_head % kFrames)

        const int kCadence = 64;
        bool g_shortRing[kCadence];
        int g_ringPos = 0, g_ringCount = 0, g_shortCount = 0;
        double g_lastRaw = -1, g_longEma = 0, g_burstStart = 0;
        int g_burstCount = 0;
        bool g_fg = false;

        void Emit(double ts, double dt)
        {
            if (dt <= 0) return;
            uint32_t i = g_head;
            g_ts[i % kFrames] = ts;
            g_ft[i % kFrames] = (float)dt;
            MemoryBarrier();
            g_head = i + 1;
        }

        void FlushBurst()
        {
            if (g_burstCount > 0 && g_longEma > 0)
                for (int i = 1; i <= g_burstCount; i++)
                    Emit(g_burstStart + g_longEma * i / g_burstCount, g_longEma / g_burstCount);
            g_burstCount = 0;
        }

        // ---- disegno ----
        FFShared::Graph g_params = {};            // copia presa insieme all'ultima immagine integra
        uint8_t* g_base = nullptr;                // righe del riquadro com'erano nell'immagine dell'app (senza linea)
        uint32_t g_baseCap = 0, g_baseW = 0, g_baseY0 = 0, g_baseY1 = 0;
        bool g_baseValid = false;
        uint32_t g_drawnHead = 0;                 // g_head all'ultimo disegno
        double g_drawnEdge = 0, g_top = 0, g_lastDrawMs = 0;
        float g_colMin[FF_OV_MAX_W], g_colMax[FF_OV_MAX_W];
        // Righe toccate in ogni colonna dall'ultimo disegno (linea e media): al frame dopo si rimettono solo
        // quei pixel, non tutto il riquadro. Ricopiare tutto il riquadro (decine di KB, spesso fuori dalla
        // cache dopo il lavoro del gioco) era la parte più cara.
        uint16_t g_lineT0[FF_OV_MAX_W], g_lineT1[FF_OV_MAX_W], g_avgT0[FF_OV_MAX_W], g_avgT1[FF_OV_MAX_W];
        bool g_touched = false;
        int g_touchC0 = 0, g_touchC1 = 0;

        void RestoreTouched(uint8_t* pixels, uint32_t w)
        {
            if (!g_touched) return;
            size_t stride = (size_t)w * 4;
            for (int x = g_touchC0; x < g_touchC1; x++)
            {
                for (int pass = 0; pass < 2; pass++)
                {
                    uint32_t r0 = pass ? g_avgT0[x] : g_lineT0[x], r1 = pass ? g_avgT1[x] : g_lineT1[x];
                    if (r0 < g_baseY0) r0 = g_baseY0;
                    if (r1 > g_baseY1) r1 = g_baseY1;
                    for (uint32_t r = r0; r < r1; r++)
                        memcpy(pixels + r * stride + (size_t)x * 4, g_base + (size_t)(r - g_baseY0) * stride + (size_t)x * 4, 4);
                }
            }
            g_touched = false;
        }

        inline double NowMs()
        {
            LARGE_INTEGER t, f;
            QueryPerformanceCounter(&t);
            QueryPerformanceFrequency(&f);
            return (double)t.QuadPart * 1000.0 / (double)f.QuadPart;
        }

        // Colora la colonna x tra y0 e y1 (frazioni di pixel sfumate) dentro [clipTop, clipBottom];
        // in t0/t1 le righe toccate.
        void Span(uint8_t* px, uint32_t width, uint32_t height, int x, double y0, double y1, double clipTop, double clipBottom,
                  uint32_t color, uint16_t& t0, uint16_t& t1, double alphaScale = 1.0)
        {
            t0 = t1 = 0;
            if (y0 < clipTop) y0 = clipTop;
            if (y1 > clipBottom) y1 = clipBottom;
            if (y1 <= y0 || x < 0 || x >= (int)width) return;
            int b = color & 0xFF, g = (color >> 8) & 0xFF, r = (color >> 16) & 0xFF;
            double inkA = ((color >> 24) & 0xFF) * alphaScale;
            int r0 = (int)floor(y0), r1 = (int)ceil(y1);
            if (r0 < 0) r0 = 0;
            if (r1 > (int)height) r1 = (int)height;
            t0 = (uint16_t)r0;
            t1 = (uint16_t)(r1 > r0 ? r1 : r0);
            int fullA = (int)(inkA + 0.5);
            uint32_t inkRB = (uint32_t)b | (uint32_t)r << 16, inkGA = (uint32_t)g | 255u << 16;
            size_t stride = (size_t)width * 4;
            uint8_t* p = px + ((size_t)r0 * width + x) * 4;
            for (int row = r0; row < r1; row++, p += stride)
            {
                // Le righe interne sono coperte del tutto: la sfumatura serve solo alla prima e all'ultima.
                int a = fullA;
                if (row == r0 || row == r1 - 1)
                {
                    double top = y0 > row ? y0 : row, bottom = y1 < row + 1 ? y1 : row + 1;
                    double cover = bottom - top;
                    if (cover <= 0) continue;
                    a = (int)(inkA * (cover < 1 ? cover : 1) + 0.5);
                }
                if (a <= 0) continue;
                // Due canali per moltiplicazione (B e R insieme, G e A insieme), poi /255 arrotondato.
                uint32_t inv = 255 - a, src;
                memcpy(&src, p, 4);
                uint32_t rb = (src & 0x00FF00FFu) * inv + inkRB * a;
                uint32_t ga = ((src >> 8) & 0x00FF00FFu) * inv + inkGA * a;
                rb += 0x00800080u; rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
                ga += 0x00800080u; ga = ((ga + ((ga >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
                uint32_t out = rb | (ga << 8);
                memcpy(p, &out, 4);
            }
        }
        // Allarga, per ogni colonna attraversata dal segmento, l'intervallo verticale occupato dalla linea.
        void Segment(double x0, double y0, double x1, double y1, int c0, int cols)
        {
            if (x1 < x0) return;
            int a = (int)floor(x0) - c0, b = (int)floor(x1) - c0;
            if (a < 0) a = 0;
            if (b > cols - 1) b = cols - 1;
            double slope = x1 > x0 ? (y1 - y0) / (x1 - x0) : 0;
            for (int c = a; c <= b; c++)
            {
                double xa = x0 > c0 + c ? x0 : c0 + c, xb = x1 < c0 + c + 1 ? x1 : c0 + c + 1;
                double ya = x1 > x0 ? y0 + (xa - x0) * slope : y0;
                double yb = x1 > x0 ? y0 + (xb - x0) * slope : y1;
                float lo = (float)(ya < yb ? ya : yb), hi = (float)(ya < yb ? yb : ya);
                if (lo < g_colMin[c]) g_colMin[c] = lo;
                if (hi > g_colMax[c]) g_colMax[c] = hi;
            }
        }

        bool ValidParams(const FFShared::Graph& p, uint32_t w, uint32_t h)
        {
            return p.enabled == 1 && p.left >= 0 && p.top >= 0 && p.right <= (float)w && p.bottom <= (float)h &&
                   p.right - p.left >= 4 && p.bottom - p.top >= 4 && p.windowMs >= 100 && p.windowMs <= 60000 &&
                   p.lineWidth > 0 && p.lineWidth < 32 && p.gridWidth >= 0 && p.gridWidth < 32 &&
                   // Anche la scala: valori assurdi (o NaN, che fallisce ogni confronto) darebbero coordinate senza senso.
                   p.fallPer100Ms >= 0 && p.fallPer100Ms < 1 && p.spikeRoom >= 1 && p.spikeRoom <= 10 &&
                   p.maxSpike >= 1 && p.maxSpike <= 1000;
        }
    }

    void GraphOnPresent(int64_t qpc, int64_t freq)
    {
        double ts = (double)qpc * 1000.0 / (double)freq;
        if (g_lastRaw < 0) { g_lastRaw = ts; return; }
        double dt = ts - g_lastRaw;
        if (dt <= 0) return;
        g_lastRaw = ts;
        if (dt >= 1000) { g_burstCount = 0; return; }   // pausa lunga (caricamento, alt-tab): non è un fotogramma

        bool isShort = g_longEma > 0 && dt < 0.35 * g_longEma;
        double longDt = g_longEma > 0 && dt > 3 * g_longEma ? 3 * g_longEma : dt;
        if (!isShort) g_longEma = longDt > g_longEma ? (g_longEma + longDt) / 2 : g_longEma * 0.9 + longDt * 0.1;

        if (g_ringCount == kCadence && g_shortRing[g_ringPos]) g_shortCount--;
        g_shortRing[g_ringPos] = isShort;
        if (isShort) g_shortCount++;
        g_ringPos = (g_ringPos + 1) % kCadence;
        if (g_ringCount < kCadence) g_ringCount++;

        double share = (double)g_shortCount / g_ringCount;   // x2 = 0,5  x3 = 0,67  x4 = 0,75
        bool fg = g_ringCount >= 32 && share >= 0.3 && share <= 0.85;
        if (fg != g_fg)
        {
            g_fg = fg;
            if (!fg) FlushBurst();
        }
        if (!fg) Emit(ts, dt);
        else if (isShort)
        {
            if (g_burstCount > 0) g_burstCount++;
            else Emit(ts, dt);
        }
        else
        {
            if (g_burstCount > 0)
            {
                double period = ts - g_burstStart;
                for (int i = 1; i <= g_burstCount; i++)
                    Emit(g_burstStart + period * i / g_burstCount, period / g_burstCount);
            }
            g_burstStart = ts;
            g_burstCount = 1;
        }
    }

    void GraphTakeParams(const FFShared::Graph& p)
    {
        g_params = p;
    }

    void GraphImageCopied(const uint8_t* pixels, uint32_t w, uint32_t h, uint32_t y0, uint32_t rows, bool full)
    {
        if (full)
        {
            // Immagine intera nuova (e parametri presi con lei): il riquadro si riprende da qui.
            g_baseValid = false;
            g_touched = false;
            if (!ValidParams(g_params, w, h)) return;
            uint32_t sy0 = (uint32_t)floor(g_params.top), sy1 = (uint32_t)ceil(g_params.bottom);
            if (sy1 > h) sy1 = h;
            size_t bytes = (size_t)w * (sy1 - sy0) * 4;
            if (bytes > g_baseCap)
            {
                // Solo quando il riquadro cresce (cambio di misura dell'overlay): non a ogni frame.
                uint8_t* bigger = (uint8_t*)HeapAlloc(GetProcessHeap(), 0, bytes);
                if (!bigger) return;
                if (g_base) HeapFree(GetProcessHeap(), 0, g_base);
                g_base = bigger;
                g_baseCap = (uint32_t)bytes;
            }
            memcpy(g_base, pixels + (size_t)sy0 * w * 4, bytes);
            g_baseW = w;
            g_baseY0 = sy0;
            g_baseY1 = sy1;
            g_baseValid = true;
            g_drawnHead = 0;   // ridisegna subito
            return;
        }
        if (!g_baseValid || w != g_baseW) return;
        // Righe riscritte dall'app che cadono nel riquadro: sono senza linea, diventano la nuova base.
        uint32_t a = y0 > g_baseY0 ? y0 : g_baseY0, b = y0 + rows < g_baseY1 ? y0 + rows : g_baseY1;
        if (a < b)
        {
            memcpy(g_base + (size_t)(a - g_baseY0) * w * 4, pixels + (size_t)a * w * 4, (size_t)(b - a) * w * 4);
            g_drawnHead = 0;
        }
    }

    bool GraphDraw(uint8_t* pixels, uint32_t w, uint32_t h, uint32_t& y0, uint32_t& y1)
    {
        if (!g_baseValid || w != g_baseW || g_baseY1 > h || !ValidParams(g_params, w, h)) return false;
        uint32_t head = g_head;
        uint32_t count = head < kFrames ? head : kFrames;
        if (count < 2) return false;
        const FFShared::Graph& p = g_params;
        double edge = g_ts[(head - 1) % kFrames];
        double left = p.left, top = p.top, right = p.right, bottom = p.bottom;
        double width = right - left, height = bottom - top;
        double window = p.windowMs;

        // Si ridisegna solo se cambia qualcosa che si vede: frame nuovi che spostano la linea di almeno
        // mezzo pixel. Fino a ~200 FPS (2,5 s su 250 pixel) vuol dire a ogni frame; sopra, la linea avanza
        // comunque di mezzo pixel alla volta e il costo resta limitato.
        if (g_drawnHead != 0)
        {
            if (head == g_drawnHead) return false;
            if ((edge - g_drawnEdge) * width / window < 0.5) return false;
        }

        // Media e massimo dei frame dentro il grafico, per la scala e la linea della media.
        double from = edge - window, sum = 0, max = 0;
        uint32_t n = 0, first = head - count;
        uint32_t i = head;
        while (i != first)
        {
            uint32_t k = (i - 1) % kFrames;
            if (g_ts[k] < from) break;
            sum += g_ft[k];
            if (g_ft[k] > max) max = g_ft[k];
            n++;
            i--;
        }
        if (n == 0) return false;
        uint32_t start = i != first ? i - 1 : i;   // un frame prima del bordo sinistro: la linea arriva al lato
        double avg = sum / n;
        double want = avg * 2;
        double spike = (max < avg * p.maxSpike ? max : avg * p.maxSpike) * p.spikeRoom;
        if (spike > want) want = spike;
        if (want < 1) want = 1;
        double nowMs = NowMs();
        if (want > g_top || g_top <= 0) g_top = want;
        else
        {
            double elapsed = g_lastDrawMs > 0 ? nowMs - g_lastDrawMs : 0;
            if (elapsed > 1000) elapsed = 1000;
            if (elapsed < 0) elapsed = 0;
            g_top = want + (g_top - want) * pow(1.0 - p.fallPer100Ms, elapsed / 100.0);
        }
        g_lastDrawMs = nowMs;

        // Si rimettono com'erano (senza linea) i pixel toccati dal disegno precedente e si disegna sopra.
        RestoreTouched(pixels, w);
        int c0 = (int)ceil(left), c1 = (int)floor(right);
        if (c0 < 0) c0 = 0;
        if (c1 > (int)w) c1 = (int)w;
        int cols = c1 - c0;
        if (cols > 0)
        {
            for (int c = 0; c < cols; c++) g_lineT0[c0 + c] = g_lineT1[c0 + c] = g_avgT0[c0 + c] = g_avgT1[c0 + c] = 0;
            g_touchC0 = c0;
            g_touchC1 = c1;
            g_touched = true;
            double avgY = bottom - height * (avg / g_top < 1 ? avg / g_top : 1);
            if (p.gridWidth > 0)
                for (int c = 0; c < cols; c++)
                    Span(pixels, w, h, c0 + c, avgY - p.gridWidth / 2, avgY + p.gridWidth / 2, top, bottom, p.gridColor,
                         g_avgT0[c0 + c], g_avgT1[c0 + c]);

            for (int c = 0; c < cols; c++) { g_colMin[c] = 3.4e38f; g_colMax[c] = -3.4e38f; }
            double prevX = 0, prevY = 0;
            bool havePrev = false;
            for (uint32_t j = start; j != head; j++)
            {
                uint32_t k = j % kFrames;
                double x = left + width * (1.0 - (edge - g_ts[k]) / window);
                double v = g_ft[k] / g_top;
                double y = bottom - height * (v < 1 ? v : 1);
                if (havePrev) Segment(prevX, prevY, x, y, c0, cols);
                prevX = x;
                prevY = y;
                havePrev = true;
            }
            double half = p.lineWidth / 2;
            for (int c = 0; c < cols; c++)
                if (g_colMin[c] <= g_colMax[c])
                    Span(pixels, w, h, c0 + c, g_colMin[c] - half, g_colMax[c] + half, top, bottom, p.lineColor,
                         g_lineT0[c0 + c], g_lineT1[c0 + c]);
        }
        g_drawnHead = head;
        g_drawnEdge = edge;
        y0 = g_baseY0;
        y1 = g_baseY1;
        return true;
    }
}
