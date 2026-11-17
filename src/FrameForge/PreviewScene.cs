using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace FrameGrip;

/// <summary>
/// Anteprima dell'overlay su uno sfondo grigio uniforme, così si vede quanto è trasparente il riquadro.
/// Sotto, la scelta "Scuro / Chiaro": il grigio chiaro serve a controllare la leggibilità sulle scene luminose.
/// La stessa in tre posti: guida iniziale, pagina Overlay e pagina Sensori (la scelta vale per tutte).
/// L'overlay si ingrandisce finché riempie il riquadro, senza deformarsi.
/// </summary>
public sealed class PreviewScene : Border
{
    private const double MaxZoom = 8;
    private static readonly Brush DarkBack = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
    private static readonly Brush LightBack = Frozen(Color.FromRgb(0xC8, 0xC8, 0xC8));

    /// <summary>Sfondo chiaro scelto (per questa sessione, uguale in tutte le anteprime).</summary>
    private static bool _light;
    private static event Action? BackdropChanged;

    private readonly Border _screen = new() { CornerRadius = new CornerRadius(10), ClipToBounds = true };
    private readonly Grid _host = new() { ClipToBounds = true, Margin = new Thickness(6) };
    private readonly Func<double>? _share;
    private readonly CheckBox? _dark, _lightBox;
    private OverlayView? _view;

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <param name="share">Quota del riquadro occupata dall'overlay (0..1). null = tutto il riquadro.</param>
    /// <param name="backdropChoice">Mostra sotto l'anteprima la scelta "Scuro / Chiaro".</param>
    public PreviewScene(Func<double>? share = null, bool backdropChoice = true)
    {
        _share = share;
        _screen.SetResourceReference(BorderBrushProperty, "Line");
        _screen.BorderThickness = new Thickness(1);
        _screen.Child = _host;
        var root = new DockPanel { LastChildFill = true };
        if (backdropChoice)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) };
            AutomationProperties.SetName(row, Loc.T("Prev.Backdrop"));
            _dark = Choice(Loc.T("Theme.Dark"), false);
            _lightBox = Choice(Loc.T("Theme.Light"), true);
            row.Children.Add(_dark);
            row.Children.Add(_lightBox);
            DockPanel.SetDock(row, Dock.Bottom);
            Loc.LanguageChanged += () =>
            {
                _dark.Content = Loc.T("Theme.Dark"); _lightBox.Content = Loc.T("Theme.Light");
                AutomationProperties.SetName(row, Loc.T("Prev.Backdrop"));
            };
            root.Children.Add(row);
        }
        root.Children.Add(_screen);
        Child = root;
        ApplyBackdrop();
        _host.SizeChanged += (_, _) => Fit();
        // le anteprime aperte seguono la scelta fatta in una qualsiasi di esse; si staccano quando escono dalla finestra
        Loaded += (_, _) => { BackdropChanged -= ApplyBackdrop; BackdropChanged += ApplyBackdrop; ApplyBackdrop(); };
        Unloaded += (_, _) => BackdropChanged -= ApplyBackdrop;
    }

    private CheckBox Choice(string text, bool light)
    {
        var c = new CheckBox { Content = text, Margin = new Thickness(4, 0, 4, 0) };
        c.SetResourceReference(StyleProperty, "Chip");
        // Checked e non Click: così funziona anche con i lettori di schermo (che commutano senza "cliccare")
        c.Checked += (_, _) => { if (_light == light) return; _light = light; BackdropChanged?.Invoke(); };
        c.Unchecked += (_, _) => ApplyBackdrop();   // la scelta attiva non si spegne: si cambia premendo l'altra
        return c;
    }

    private void ApplyBackdrop()
    {
        _screen.Background = _light ? LightBack : DarkBack;
        if (_dark != null) _dark.IsChecked = !_light;
        if (_lightBox != null) _lightBox.IsChecked = _light;
    }

    /// <summary>Mette nel riquadro l'overlay da mostrare (lo toglie da dove stava prima).</summary>
    public void Show(OverlayView view)
    {
        if (_view == view) return;
        if (view.Parent is Panel p) p.Children.Remove(view);
        else if (view.Parent is Decorator d) d.Child = null;
        else if (view.Parent is ContentControl c) c.Content = null;
        if (_view != null) { _view.SizeChanged -= ViewSized; _host.Children.Remove(_view); }
        _view = view;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Center;
        view.SizeChanged += ViewSized;
        _host.Children.Add(view);
        Fit();
    }

    private void ViewSized(object sender, SizeChangedEventArgs e) => Fit();

    /// <summary>La scala più grande con cui l'overlay sta tutto nel riquadro. Da richiamare quando cambia la grandezza del testo.</summary>
    public void Fit()
    {
        var v = _view;
        if (v == null) return;
        // misure senza ingrandimento: ActualWidth/Height non contano la LayoutTransform
        double w = v.ActualWidth, h = v.ActualHeight;
        if (w <= 0 || h <= 0 || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;
        double fit = Math.Min(MaxZoom, Math.Min(_host.ActualWidth / w, _host.ActualHeight / h));
        double zoom = fit * Math.Clamp(_share?.Invoke() ?? 1.0, 0.2, 1.0);
        if (v.LayoutTransform is ScaleTransform t && Math.Abs(t.ScaleX - zoom) < 0.005) return;
        v.LayoutTransform = new ScaleTransform(zoom, zoom);
    }
}
