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

    private void ApplyScale() =>
        View.LayoutTransform = new ScaleTransform(_settings.Scale, _settings.Scale);

    public void Reposition()
    {
        var area = SystemParameters.WorkArea;
        bool right = _settings.Corner is 1 or 3;
        bool bottom = _settings.Corner is 2 or 3;
        Left = right ? area.Right - ActualWidth - EdgeMargin : area.Left + EdgeMargin;
        if (_settings.Corner == 4) Left = area.Left + (area.Width - ActualWidth) / 2;   // in alto al centro
        Top = bottom ? area.Bottom - ActualHeight - EdgeMargin : area.Top + EdgeMargin;
    }

    /// <summary>I giochi senza bordi possono coprire l'overlay: lo si riporta in cima.</summary>
    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero && IsVisible)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
