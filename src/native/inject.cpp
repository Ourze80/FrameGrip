// ffinject: carica la DLL di FrameGrip in un processo.
// Uso: ffinject <pid> <percorso completo della DLL>
// Codici di uscita: 0 ok, 2 argomenti, 3 processo non apribile, 4 architettura diversa,
//                   5 memoria, 6 thread remoto, 7 la DLL non si è caricata,
//                   8 il caricamento non è finito in tempo (il gioco è bloccato o molto lento).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cwchar>
#include <cstdlib>
#include "shared.h"
#pragma comment(lib, "advapi32.lib")

int wmain(int argc, wchar_t** argv)
{
    if (argc < 3) return 2;
    DWORD pid = wcstoul(argv[1], nullptr, 10);
    const wchar_t* dll = argv[2];

    HANDLE process = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ,
        FALSE, pid);
    if (!process) return 3;

    // L'iniettore deve avere la stessa architettura (32/64 bit) del gioco.
    BOOL targetWow = FALSE, selfWow = FALSE;
    IsWow64Process(process, &targetWow);
    IsWow64Process(GetCurrentProcess(), &selfWow);
    if (targetWow != selfWow) { CloseHandle(process); return 4; }

    SIZE_T bytes = (wcslen(dll) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remote || !WriteProcessMemory(process, remote, dll, bytes, nullptr)) { CloseHandle(process); return 5; }

    // Segnale "iniezione" per la DLL (vedi DllMain): esiste finché il caricamento non è finito.
    // Accesso per tutti (DACL nulla): il gioco può girare con un utente o un livello diverso dall'app.
    wchar_t markerName[64];
    swprintf_s(markerName, FF_INJECT_MARKER_PREFIX L"%lu", pid);
    SECURITY_DESCRIPTOR sd;
    SECURITY_ATTRIBUTES sa = { sizeof(sa), nullptr, FALSE };
    if (InitializeSecurityDescriptor(&sd, SECURITY_DESCRIPTOR_REVISION) && SetSecurityDescriptorDacl(&sd, TRUE, nullptr, FALSE))
        sa.lpSecurityDescriptor = &sd;
    HANDLE marker = CreateEventW(&sa, TRUE, FALSE, markerName);

    // kernel32 è caricata allo stesso indirizzo in tutti i processi della stessa architettura.
    auto loadLibrary = (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
    if (!thread)
    {
        if (marker) CloseHandle(marker);
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);
        return 6;
    }

    if (WaitForSingleObject(thread, 10000) != WAIT_OBJECT_0)
    {
        // LoadLibraryW sta ancora leggendo il percorso: il buffer non si libera (meglio pochi byte persi
        // nel gioco che un caricamento da memoria liberata). Il segnale si chiude con l'iniettore.
        CloseHandle(thread);
        if (marker) CloseHandle(marker);
        CloseHandle(process);
        return 8;
    }
    DWORD result = 0;
    GetExitCodeThread(thread, &result);
    CloseHandle(thread);
    if (marker) CloseHandle(marker);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    CloseHandle(process);
    return result ? 0 : 7;
}
