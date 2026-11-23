// Blocco di memoria condivisa tra l'app e la DLL caricata nel gioco.
// Lo crea la DLL (un blocco per processo), l'app lo apre in lettura/scrittura.
// ATTENZIONE: gli offset sono replicati in src/FrameGrip/Core/GameHook.cs.
// Solo tipi a dimensione fissa: il layout è identico a 32 e 64 bit.
#pragma once
#include <cstdint>
#include <cstddef>

#define FF_SHARED_PREFIX L"Local\\FrameGripHook_"   // seguito dal PID in decimale
// Segnale creato da ffinject prima di caricare la DLL nel gioco (seguito dal PID): così la DLL sa di
// essere stata iniettata e non caricata come layer Vulkan. Solo nativo: nessun effetto sul layout.
#define FF_INJECT_MARKER_PREFIX L"Local\\FrameGripInject"
// 3: righe cambiate (ovFullSeq, ovDirtyY, ovDirtyRows) e immagine spostata a 16480.
// L'app sa usare anche la 2 (solo immagini intere); un'app che conosce solo la 2 rifiuta la 3.
// Le aggiunte compatibili non cambiano la versione: la DLL le annuncia in dllCaps e l'app le usa
// solo se le vede. Così un'app della versione 3 di prima funziona con la DLL nuova (e viceversa).
#define FF_SHARED_VERSION 3
#define FF_CAP_GRAPH 1u   // la DLL disegna da sé, a ogni frame, la linea del grafico del frametime (FFGraph)
#define FF_CAP_HDR 2u     // la DLL converte l'overlay per i giochi HDR e legge hdrWhiteNits
#define FF_RING 2048
#define FF_OV_MAX_W 2048
#define FF_OV_MAX_H 1024

// Valori di FFShared::api
#define FF_API_NONE 0
#define FF_API_DXGI 1   // DirectX 10/11/12, versione non riconosciuta
#define FF_API_D3D12 2
#define FF_API_D3D9 3
#define FF_API_OPENGL 4
#define FF_API_VULKAN 5
#define FF_API_D3D11 6
#define FF_API_D3D10 7

#pragma pack(push, 8)
struct FFShared
{
    uint32_t version;         //     0
    uint32_t api;             //     4  ultima API che ha presentato un frame

    // --- scritto dall'app ---
    uint32_t limiterEnabled;  //     8
    uint32_t overlayVisible;  //    12
    double   targetFps;       //    16
    uint64_t heartbeatMs;     //    24  GetTickCount64 dell'app: se si ferma, limitatore e overlay si spengono

    // --- scritto dalla DLL ---
    uint64_t writeIndex;      //    32  numero totale di frame registrati
    int64_t  qpcFrequency;    //    40
    int64_t  ring[FF_RING];   //    48  istante (QPC) di ogni Present

    // --- immagine dell'overlay, scritta dall'app ---
    uint32_t ovSeq;           // 16432  dispari mentre l'app sta scrivendo
    uint32_t ovWidth;         // 16436
    uint32_t ovHeight;        // 16440
    uint32_t ovCorner;        // 16444  0 alto-sx, 1 alto-dx, 2 basso-sx, 3 basso-dx, 4 alto-centro
    uint32_t ovMargin;        // 16448  distanza dal bordo, in pixel
    uint32_t ovFullSeq;       // 16452  valore di ovSeq alla fine dell'ultima scrittura dell'immagine intera
    uint64_t lastDrawMs;      // 16456  (DLL) GetTickCount64 dell'ultimo overlay disegnato nel gioco
    uint32_t ovDirtyY;        // 16464  le scritture parziali dopo ovFullSeq toccano solo le righe
    uint32_t ovDirtyRows;     // 16468  [ovDirtyY, ovDirtyY + ovDirtyRows)
    uint32_t dllCaps;         // 16472  (DLL) FF_CAP_*: cosa sa fare questa DLL in più della versione base
    float    hdrWhiteNits;    // 16476  (app) bianco dell'overlay nei giochi HDR, in nit; 0 = 200 (app vecchie)
    uint8_t  ovPixels[FF_OV_MAX_W * FF_OV_MAX_H * 4]; // 16480  BGRA premoltiplicato, righe da ovWidth*4 byte

    // --- grafico del frametime disegnato dalla DLL (solo con FF_CAP_GRAPH) ---
    // Scritto dall'app insieme all'immagine intera, dentro la stessa scrittura protetta da ovSeq:
    // la DLL lo legge solo quando copia un'immagine integra, così riquadro e immagine vanno sempre d'accordo.
    struct Graph
    {
        uint32_t enabled;     // +0   1 = la DLL disegna la linea (l'app allora non la mette nell'immagine)
        float left, top, right, bottom;   // +4  interno del riquadro, in pixel dell'immagine
        uint32_t lineColor;   // +20  byte B, G, R, A (alfa non premoltiplicato)
        uint32_t gridColor;   // +24  colore della linea della media
        float lineWidth;      // +28  in pixel
        float gridWidth;      // +32
        float windowMs;       // +36  tempo mostrato dal grafico
        float fallPer100Ms;   // +40  scala: quanto scende ogni 100 ms (sale subito)
        float spikeRoom;      // +44  margine sopra il picco
        float maxSpike;       // +48  i picchi fino a maxSpike volte la media entrano interi
        uint32_t reserved[3]; // +52
    } graph;                  // 8405088
};
#pragma pack(pop)
static_assert(offsetof(FFShared, ovFullSeq) == 16452 && offsetof(FFShared, ovDirtyY) == 16464 &&
              offsetof(FFShared, dllCaps) == 16472 && offsetof(FFShared, ovPixels) == 16480 &&
              offsetof(FFShared, graph) == 8405088 && sizeof(FFShared::Graph) == 64, "layout diverso da GameHook.cs");
