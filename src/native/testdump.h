// Parte comune dei giochi di prova: salva il fotogramma letto dalla scheda video insieme all'immagine
// dell'overlay che l'app ha messo nella memoria condivisa in quel momento, per confrontarli fuori
// (scratch: controllo dei colori HDR). File %TEMP%\ff_dump_<nome>.bin:
//   "FFDUMP1\0", w, h, formato (codice dell'API), byte per pixel, pitch,
//   ovW, ovH, ovCorner, ovMargin, poi le righe del fotogramma e i pixel dell'overlay (BGRA premoltiplicato).
#pragma once
#include <windows.h>
#include <cstdio>
#include <cstdint>
#include <cwchar>
#include "shared.h"

// Solo per le prove: se FF_TEST_HOOK_DLL indica la DLL di FrameGrip, il gioco di prova la carica da sé
// all'avvio (come farebbe ffinject), così la prova non dipende dall'iniettore.
inline void LoadTestHook()
{
    wchar_t path[MAX_PATH];
    DWORD n = GetEnvironmentVariableW(L"FF_TEST_HOOK_DLL", path, MAX_PATH);
    if (n && n < MAX_PATH) LoadLibraryW(path);
}

inline void WriteTestDump(const wchar_t* name, const void* rows, uint32_t w, uint32_t h, uint32_t format,
                          uint32_t bytesPerPixel, uint32_t pitch)
{
    wchar_t mapName[64];
    swprintf_s(mapName, FF_SHARED_PREFIX L"%lu", GetCurrentProcessId());
    HANDLE map = OpenFileMappingW(FILE_MAP_READ, FALSE, mapName);
    const FFShared* s = map ? (const FFShared*)MapViewOfFile(map, FILE_MAP_READ, 0, 0, sizeof(FFShared)) : nullptr;
    uint32_t head[10] = { w, h, format, bytesPerPixel, pitch, 0, 0, 0, 0, 0 };
    if (s && s->ovWidth <= FF_OV_MAX_W && s->ovHeight <= FF_OV_MAX_H)
    {
        head[5] = s->ovWidth; head[6] = s->ovHeight; head[7] = s->ovCorner; head[8] = s->ovMargin;
    }
    wchar_t path[MAX_PATH];
    GetTempPathW(MAX_PATH, path);
    wcscat_s(path, L"ff_dump_");
    wcscat_s(path, name);
    wcscat_s(path, L".bin");
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") == 0 && f)
    {
        fwrite("FFDUMP1", 8, 1, f);
        fwrite(head, sizeof(head), 1, f);
        for (uint32_t y = 0; y < h; y++)
            fwrite((const uint8_t*)rows + (size_t)y * pitch, bytesPerPixel, w, f);
        if (head[5]) fwrite(s->ovPixels, 4, (size_t)head[5] * head[6], f);
        fclose(f);
    }
    if (s) UnmapViewOfFile(s);
    if (map) CloseHandle(map);
}
