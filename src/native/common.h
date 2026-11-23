// Parti comuni della DLL di aggancio, usate da tutte le API grafiche.
#pragma once
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include "shared.h"

struct IDXGISwapChain;

namespace ff
{
    extern FFShared* g_shared;

    // Preparazione comune (frequenza QPC, memoria condivisa): una volta sola, sicura tra thread.
    bool InitCore();
    // Modo layer Vulkan, layer accettato: blocca la DLL in memoria e fa la preparazione comune.
    bool StartLayerMode();

    // Immagine dell'overlay e texture: un solo thread alla volta. Nel Present si usa solo il tentativo
    // (se è occupato si salta l'overlay per quel frame, mai un'attesa); fuori dal Present si può aspettare.
    extern SRWLOCK g_overlayLock;
    struct OverlayTryLock
    {
        bool locked;
        OverlayTryLock() : locked(TryAcquireSRWLockExclusive(&g_overlayLock) != 0) {}
        ~OverlayTryLock() { if (locked) ReleaseSRWLockExclusive(&g_overlayLock); }
        OverlayTryLock(const OverlayTryLock&) = delete;
        OverlayTryLock& operator=(const OverlayTryLock&) = delete;
    };

    // Da chiamare una volta per frame, subito prima del Present originale:
    // applica il limitatore e registra l'istante del frame.
    void OnFrame(uint32_t api);
    // Falso se un'altra API sta già presentando i frame del gioco (es. Vulkan HDR presentato dal driver
    // tramite DXGI): allora l'overlay lo disegna lei, qui si salta (niente overlay doppio).
    bool OwnsFrames(uint32_t api);
    // Solo per il diario (riga "Stato" di OnFrame): Present che non sono passati dal limitatore perché
    // di uno swapchain non principale, o perché nuovo tentativo di un Present rifiutato (hook_dxgi.cpp).
    extern volatile LONG g_diagNotPrimary, g_diagRetry;

    // Immagine dell'overlay preparata dall'app (BGRA premoltiplicato).
    struct Overlay
    {
        uint32_t width, height, corner, margin;
        const uint8_t* pixels;
        uint32_t serial;   // cambia quando cambia il contenuto: serve a ricaricare la texture solo allora
        float whiteNits;   // luminanza del bianco dell'overlay quando il gioco presenta in HDR
    };

    // Come vanno scritti i colori dell'overlay (immagine sRGB, alfa premoltiplicato) nell'immagine del gioco.
    // Si sceglie solo quando cambiano formato o spazio colore; nel pixel shader è una costante.
    enum ColorMode : uint32_t
    {
        kColorSdr = 0,       // immagine SDR normale: i valori si copiano così come sono
        kColorSdrSrgb = 1,   // vista sRGB: la scheda video ricodifica, quindi si passa il colore già lineare
        kColorScRgb = 2,     // scRGB (16 bit float, lineare, 1.0 = 80 nit)
        kColorHdr10 = 3,     // HDR10 (10 bit, primari BT.2020, curva PQ)
    };
    struct ColorConsts { float mode, scale, pad0, pad1; };   // = cbuffer / costanti del pixel shader
    ColorConsts MakeColorConsts(uint32_t mode, float whiteNits);
    const char* ColorModeName(uint32_t mode);
    // Modo per uno swapchain DXGI con questo formato del back buffer (hook_dxgi.cpp). Non aspetta la scheda video.
    uint32_t DxgiColorMode(::IDXGISwapChain* sc, uint32_t dxgiFormat);
    // Pixel shader HLSL comune a Direct3D 11 e 12 (hook_dxgi.cpp).
    extern const char kOverlayHlsl[];
    extern const unsigned kOverlayHlslSize;
    bool GetOverlay(Overlay& out);   // false = niente da disegnare
    // Righe [y0, y1) da ricaricare in una texture che contiene l'immagine con numero di serie texSerial.
    // fresh = texture appena creata (o di cui non si sa il contenuto): allora tutte.
    void RowsToUpload(const Overlay& ov, uint32_t texSerial, bool fresh, uint32_t& y0, uint32_t& y1);
    // Grafico del frametime disegnato dalla DLL (hook_graph.cpp).
    void GraphOnPresent(int64_t qpc, int64_t freq);                 // un frame presentato
    void GraphTakeParams(const FFShared::Graph& p);                 // parametri arrivati con un'immagine intera
    void GraphImageCopied(const uint8_t* pixels, uint32_t w, uint32_t h, uint32_t y0, uint32_t rows, bool full);
    bool GraphDraw(uint8_t* pixels, uint32_t w, uint32_t h, uint32_t& y0, uint32_t& y1);   // true = righe [y0, y1) ridisegnate
    void Place(const Overlay& o, uint32_t targetW, uint32_t targetH, int& x, int& y);
    void MarkDrawn();

    // Diario dell'aggancio (poche righe per gioco) in %LOCALAPPDATA%\FrameGrip\hook.log: serve per l'assistenza.
    void Log(const char* format, ...);

    bool PatchVtable(void** vtable, int index, void* hook, void** original);
    HWND CreateProbeWindow(HMODULE self);
    void DestroyProbeWindow(HWND hwnd, HMODULE self);

    bool HookDxgi(HMODULE self);   // hook_dxgi.cpp
    // Fuori dal Present (thread dell'aggancio, una volta al secondo): controlla che gli agganci nelle tabelle
    // di DXGI siano ancora al loro posto e rimette quelli che un altro programma ha tolto.
    void DxgiMaintain();
    // Devia il codice di una funzione (MinHook). "original" riceve il modo per chiamare quella vera.
    bool InlineHook(const char* name, void* target, void* hook, void** original);
    // Altri overlay a volte sostituiscono il nostro aggancio sul codice: lo si annota nel diario
    // (una volta) e lo si lascia com'è.
    void CheckInlineHooks();

    bool HookD3D12();              // hook_d3d12.cpp: scopre la coda di comandi del gioco
    void DrawD3D12(::IDXGISwapChain* sc);
    // Fuori dal Present (thread dell'aggancio, una volta al secondo): libera code e risorse DirectX 12
    // di device rimossi o che il gioco ha già buttato, così può ricreare il device (TDR).
    void D3D12Maintain();

    bool HookD3D9(HMODULE self);   // hook_d3d9.cpp
    bool HookOpenGL();             // hook_gl.cpp
}
