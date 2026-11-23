using System.Runtime.InteropServices;

namespace FrameGrip.Core;

public enum WindowMode { Unknown, Fullscreen, Borderless, Windowed }

/// <summary>
/// Frequenza dello schermo su cui sta il gioco e modo della finestra (schermo intero, senza bordi, in finestra).
/// Solo letture di Windows, poche volte al secondo al massimo: niente nel processo del gioco.
/// </summary>
public static class DisplayInfo
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public short SpecVersion, DriverVersion, Size, DriverExtra;
        public int Fields;
        public int PositionX, PositionY, DisplayOrientation, DisplayFixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public short LogPixels;
        public int BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency;
        public int IcmMethod, IcmIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Pt pt, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct Pt { public int X, Y; }
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] p, ref uint modes, [Out] ModeInfo[] m, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceName req);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetName req);

    // strutture di QueryDisplayConfig: servono per il nome vero dello schermo (quello che ha scritto il produttore) e per la frequenza esatta
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct PathSource { public Luid Adapter; public uint Id, ModeIdx, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PathTarget
    {
        public Luid Adapter; public uint Id, ModeIdx, Tech, Rotation, Scaling;
        public uint RefreshNum, RefreshDen, ScanLine; public int Available; public uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, Size = 64)] private struct ModeInfo { public uint InfoType, Id; public Luid Adapter; }
    [StructLayout(LayoutKind.Sequential)] private struct InfoHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceName { public InfoHeader Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetName
    {
        public InfoHeader Header; public uint Flags, Tech; public ushort Manufacturer, Product; public uint Instance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }

    /// <summary>Schermo principale: nome, risoluzione e frequenza. Nome vuoto se Windows non lo dice.</summary>
    public readonly record struct Screen(string Name, int Width, int Height, int Hz);

    public static Screen Primary()
    {
        try
        {
            var mon = MonitorFromPoint(new Pt(), 1 /* MONITOR_DEFAULTTOPRIMARY */);
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(mon, ref mi)) return default;
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            if (!EnumDisplaySettings(mi.Device, ENUM_CURRENT_SETTINGS, ref dm)) return new Screen(FriendlyName(mi.Device), 0, 0, 0);
            return new Screen(FriendlyName(mi.Device), dm.PelsWidth, dm.PelsHeight, dm.DisplayFrequency > 1 ? dm.DisplayFrequency : 0);
        }
        catch { return default; }
    }

    // Il nome scritto nello schermo (per esempio "LG ULTRAGEAR"): si cerca il percorso attivo che parte da questo \\.\DISPLAYn.
    private static string FriendlyName(string gdi)
    {
        try
        {
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return "";
            var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return "";
            for (int i = 0; i < np; i++)
            {
                var src = new SourceName { Header = new InfoHeader { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id } };
                if (DisplayConfigGetDeviceInfo(ref src) != 0 || !string.Equals(src.Gdi, gdi, StringComparison.OrdinalIgnoreCase)) continue;
                var tgt = new TargetName { Header = new InfoHeader { Type = 2, Size = (uint)Marshal.SizeOf<TargetName>(), Adapter = paths[i].Target.Adapter, Id = paths[i].Target.Id } };
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0 && !string.IsNullOrWhiteSpace(tgt.Friendly)) return tgt.Friendly.Trim();
            }
        }
        catch { }
        return "";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DevMode dm);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    private const int ENUM_CURRENT_SETTINGS = -1, GWL_STYLE = -16, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;

    /// <summary>Frequenza dello schermo della finestra (Hz), 0 se non si riesce a leggerla.</summary>
    public static int RefreshHz(IntPtr hwnd)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(mon, ref mi)) return 0;
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(mi.Device, ENUM_CURRENT_SETTINGS, ref dm) && dm.DisplayFrequency > 1 ? dm.DisplayFrequency : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Frequenza esatta dello schermo della finestra (per esempio 59,94 o 143,98 Hz), dal rapporto che usa Windows
    /// (QueryDisplayConfig); se non si legge, quella intera di RefreshHz. 0 se non si riesce a leggere niente.
    /// I valori a un centesimo da un intero (60000/1000.2...) diventano l'intero.
    /// </summary>
    public static double RefreshRate(IntPtr hwnd)
    {
        try
        {
            var mon = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(mon, ref mi)) return 0;
            double exact = ExactRefresh(mi.Device);
            if (exact > 1) return exact;
            var dm = new DevMode { Size = (short)Marshal.SizeOf<DevMode>() };
            return EnumDisplaySettings(mi.Device, ENUM_CURRENT_SETTINGS, ref dm) && dm.DisplayFrequency > 1 ? dm.DisplayFrequency : 0;
        }
        catch { return 0; }
    }

    /// <summary>Frequenza del percorso attivo che parte da questo \\.\DISPLAYn (numeratore/denominatore), 0 se non c'è.</summary>
    private static double ExactRefresh(string gdi)
    {
        const uint QDC_ONLY_ACTIVE_PATHS = 2;
        if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return 0;
        var paths = new PathInfo[np]; var modes = new ModeInfo[nm];
        if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return 0;
        for (int i = 0; i < np; i++)
        {
            var src = new SourceName { Header = new InfoHeader { Type = 1, Size = (uint)Marshal.SizeOf<SourceName>(), Adapter = paths[i].Source.Adapter, Id = paths[i].Source.Id } };
            if (DisplayConfigGetDeviceInfo(ref src) != 0 || !string.Equals(src.Gdi, gdi, StringComparison.OrdinalIgnoreCase)) continue;
            return Normalize(paths[i].Target.RefreshNum, paths[i].Target.RefreshDen);
        }
        return 0;
    }

    /// <summary>Numeratore/denominatore in Hz; a un centesimo da un intero si arrotonda (niente 143,9999).</summary>
    public static double Normalize(uint num, uint den)
    {
        if (num == 0 || den == 0) return 0;
        double hz = (double)num / den;
        double round = Math.Round(hz);
        return Math.Abs(hz - round) < 0.01 ? round : hz;
    }

    /// <summary>
    /// Schermo intero "vero" se Windows dice che c'è un'app Direct3D a schermo intero in primo piano; altrimenti
    /// "senza bordi" se la finestra copre tutto lo schermo senza cornice, "in finestra" negli altri casi.
    /// </summary>
    public static WindowMode Mode(IntPtr hwnd, bool foreground)
    {
        if (hwnd == IntPtr.Zero) return WindowMode.Unknown;
        try
        {
            if (foreground && SHQueryUserNotificationState(out int st) == 0 && st == QUNS_RUNNING_D3D_FULL_SCREEN) return WindowMode.Fullscreen;
            var mon = MonitorFromWindow(hwnd, 2);
            var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(mon, ref mi) || !GetWindowRect(hwnd, out var r)) return WindowMode.Unknown;
            int style = GetWindowLong(hwnd, GWL_STYLE);
            bool covers = r.L <= mi.Monitor.L && r.T <= mi.Monitor.T && r.R >= mi.Monitor.R && r.B >= mi.Monitor.B;
            bool frameless = (style & (WS_CAPTION | WS_THICKFRAME)) == 0;
            return covers && frameless ? WindowMode.Borderless : WindowMode.Windowed;
        }
        catch { return WindowMode.Unknown; }
    }
}
