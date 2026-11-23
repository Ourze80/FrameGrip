using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>Finestra trasparente, sempre in primo piano e "attraversabile" dal mouse.</summary>
public sealed class OverlayWindow : Window
{
    private const double EdgeMargin = 16;
    private readonly Settings _settings;
    private IntPtr _hwnd;

    public OverlayView View { get; } = new();

    public OverlayWindow(Settings settings)
    {
        _settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "FrameGrip Overlay";

        View.Settings = settings;
        Content = View;
        ApplyScale();
        SizeChanged += (_, _) => Reposition();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        ex |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));
        Reposition();
    }

    public void ApplySettings()
    {
        ApplyScale();
        View.Refresh();
        Reposition();
    }

    private readonly ScaleTransform _scale = new();

    // la stessa trasformazione si riusa: prima se ne creava una nuova a ogni modifica delle impostazioni
    private void ApplyScale()
    {
        if (_scale.ScaleX != _settings.Scale) _scale.ScaleX = _settings.Scale;
        if (_scale.ScaleY != _settings.Scale) _scale.ScaleY = _settings.Scale;
        if (View.LayoutTransform != _scale) View.LayoutTransform = _scale;
    }

    private IntPtr _gameWindow;

    /// <summary>La finestra del gioco mostrato (zero se non c'è): l'overlay va nell'angolo del suo schermo.</summary>
    public IntPtr GameWindow
    {
        get => _gameWindow;
        set
        {
            if (_gameWindow == value) return;
            _gameWindow = value;
            if (IsVisible) Reposition();
        }
    }

    public void Reposition()
    {
        // Gioco su un altro schermo (non il principale): l'overlay va lì, in pixel veri.
        if (_hwnd != IntPtr.Zero && _gameWindow != IntPtr.Zero && RepositionOnGameMonitor()) return;
        var area = SystemParameters.WorkArea;
        bool right = _settings.Corner is 1 or 3;
        bool bottom = _settings.Corner is 2 or 3;
        Left = right ? area.Right - ActualWidth - EdgeMargin : area.Left + EdgeMargin;
        if (_settings.Corner == 4) Left = area.Left + (area.Width - ActualWidth) / 2;   // in alto al centro
        Top = bottom ? area.Bottom - ActualHeight - EdgeMargin : area.Top + EdgeMargin;
    }

    private bool RepositionOnGameMonitor()
    {
        IntPtr mon = Native.MonitorFromWindow(_gameWindow, Native.MONITOR_DEFAULTTONULL);
        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        if (mon == IntPtr.Zero || !Native.GetMonitorInfo(mon, ref info)) return false;
        if ((info.Flags & 1 /* MONITORINFOF_PRIMARY */) != 0) return false;   // schermo principale: come sempre
        if (!Native.GetWindowRect(_hwnd, out var r)) return false;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = r.Right - r.Left, h = r.Bottom - r.Top, margin = (int)Math.Round(EdgeMargin * scale);
        var area = info.Work;
        bool right = _settings.Corner is 1 or 3;
        bool bottom = _settings.Corner is 2 or 3;
        int x = right ? area.Right - w - margin : area.Left + margin;
        if (_settings.Corner == 4) x = area.Left + (area.Right - area.Left - w) / 2;   // in alto al centro
        int y = bottom ? area.Bottom - h - margin : area.Top + margin;
        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        return true;
    }

    /// <summary>I giochi senza bordi possono coprire l'overlay: lo si riporta in cima.</summary>
    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
