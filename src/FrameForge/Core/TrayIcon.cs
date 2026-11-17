using System.Runtime.InteropServices;

namespace FrameGrip.Core;

/// <summary>Icona dell'app nell'area di notifica (vicino all'orologio), senza dipendere da Windows Forms.</summary>
public sealed class TrayIcon : IDisposable
{
    public const int CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    public const int WM_LBUTTONUP = 0x202, WM_LBUTTONDBLCLK = 0x203, WM_RBUTTONUP = 0x205;

    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int ExtractIconExW(string file, int index, out IntPtr large, out IntPtr small, int count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessageW(string name);

    /// <summary>Windows lo manda quando la barra riparte (per esempio dopo un riavvio di Esplora risorse): l'icona va rimessa.</summary>
    public static readonly int TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly string _tip;
    private IntPtr _large, _small;
    private bool _added;

    public TrayIcon(IntPtr hwnd, string tip)
    {
        _hwnd = hwnd;
        _tip = tip;
        string? exe = Environment.ProcessPath;
        if (exe != null) ExtractIconExW(exe, 0, out _large, out _small, 1);
        Add();
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = CallbackMessage,
        hIcon = _small != IntPtr.Zero ? _small : _large,
        szTip = _tip,
        szInfo = "",
        szInfoTitle = "",
    };

    public void Add()
    {
        var d = Data();
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
    }

    /// <summary>Notifica di Windows accanto all'icona: serve quando nel gioco non si può disegnare niente.</summary>
    public void Notify(string title, string text)
    {
        if (!_added) return;
        var d = Data();
        d.uFlags |= NIF_INFO;
        d.szInfoTitle = title.Length > 63 ? title[..63] : title;
        d.szInfo = text.Length > 255 ? text[..255] : text;
        d.dwInfoFlags = 1;   // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public void Dispose()
    {
        if (_added)
        {
            var d = Data();
            Shell_NotifyIconW(NIM_DELETE, ref d);
            _added = false;
        }
        if (_small != IntPtr.Zero) DestroyIcon(_small);
        if (_large != IntPtr.Zero) DestroyIcon(_large);
        _small = _large = IntPtr.Zero;
    }
}
