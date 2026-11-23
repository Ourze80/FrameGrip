using System.Runtime.InteropServices;

namespace FrameGrip;

internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Finestra ridotta a icona e schermi: servono per riaprire la finestra dall'icona vicino all'orologio.
    public const int SW_RESTORE = 9;
    public const uint SWP_NOZORDER = 0x4;
    public const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

    /// <summary>Barra del titolo scura e dello stesso colore della finestra (il colore vale da Windows 11).</summary>
    public static void DarkTitleBar(IntPtr hWnd, byte r, byte g, byte b) =>
        TitleBar(hWnd, true, r, g, b, 0xF2, 0xF4, 0xF7);

    /// <summary>Barra del titolo scura o chiara, con lo sfondo e il colore del testo indicati (il colore vale da Windows 11).</summary>
    public static void TitleBar(IntPtr hWnd, bool dark, byte r, byte g, byte b, byte tr, byte tg, byte tb)
    {
        try
        {
            int on = dark ? 1 : 0;
            DwmSetWindowAttribute(hWnd, 20, ref on, sizeof(int));        // DWMWA_USE_IMMERSIVE_DARK_MODE
            int color = r | (g << 8) | (b << 16);
            DwmSetWindowAttribute(hWnd, 35, ref color, sizeof(int));     // DWMWA_CAPTION_COLOR
            DwmSetWindowAttribute(hWnd, 34, ref color, sizeof(int));     // DWMWA_BORDER_COLOR
            int text = tr | (tg << 8) | (tb << 16);
            DwmSetWindowAttribute(hWnd, 36, ref text, sizeof(int));      // DWMWA_TEXT_COLOR
        }
        catch
        {
            // Windows più vecchi: la barra resta quella di sistema.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool IsWow64Process(IntPtr process, out bool wow64);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref uint info, int size);
    [DllImport("kernel32.dll")] private static extern bool SetThreadInformation(IntPtr thread, int infoClass, ref uint info, int size);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref uint info, int size, out int returned);
    [DllImport("ntdll.dll")] private static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref uint info, int size);

    /// <summary>
    /// Riporta il processo a priorità normale (CPU, memoria, disco). Serve quando l'app parte dall'attività
    /// pianificata dell'avvio automatico: Windows avvia le attività a priorità "sotto il normale", con memoria
    /// e disco a priorità bassa. Con la CPU tutta occupata dal gioco (compilazione degli shader, caricamenti)
    /// il thread dell'overlay restava fermo per secondi: il "battito" non arrivava alla DLL nel gioco, che dopo
    /// 3 secondi spegne limitatore e overlay credendo l'app chiusa. Da chiamare per prima cosa, prima che
    /// nascano gli altri thread. Restituisce cosa ha cambiato (per il diario), null se era già tutto normale.
    /// </summary>
    public static string? RestoreNormalPriority()
    {
        try
        {
            const int processMemoryPriority = 0, threadMemoryPriority = 0, processIoPriority = 33;
            const uint memoryNormal = 5, ioNormal = 2;
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            var cls = me.PriorityClass;
            IntPtr h = GetCurrentProcess();
            uint mem = memoryNormal, io = ioNormal;
            bool memKnown = GetProcessInformation(h, processMemoryPriority, ref mem, 4);
            bool ioKnown = NtQueryInformationProcess(h, processIoPriority, ref io, 4, out _) == 0;
            bool lowCpu = cls is System.Diagnostics.ProcessPriorityClass.Idle or System.Diagnostics.ProcessPriorityClass.BelowNormal;
            bool lowMem = memKnown && mem < memoryNormal, lowIo = ioKnown && io < ioNormal;
            if (!lowCpu && !lowMem && !lowIo) return null;

            if (lowCpu) me.PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal;
            uint value = memoryNormal;
            if (lowMem)
            {
                SetProcessInformation(h, processMemoryPriority, ref value, 4);
                SetThreadInformation(GetCurrentThread(), threadMemoryPriority, ref value, 4);   // questo thread è nato prima
            }
            value = ioNormal;
            if (lowIo) NtSetInformationProcess(h, processIoPriority, ref value, 4);
            return $"was {cls}, memory {(memKnown ? mem.ToString() : "?")}, I/O {(ioKnown ? io.ToString() : "?")}: set to normal";
        }
        catch (Exception ex)
        {
            return "not changed: " + ex.Message;
        }
    }
}
