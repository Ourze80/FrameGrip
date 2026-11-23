using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FrameGrip;

/// <summary>
/// Selettore dei colori condiviso: lo usano la finestra principale (colori delle voci dell'overlay)
/// e le Opzioni (colore dell'app). Ogni finestra lo mette nel suo Popup; qui c'è l'unica copia dei calcoli.
/// Ogni scelta si applica subito e il selettore resta aperto: si chiude solo con "Done".
/// </summary>
public partial class ColorPicker : UserControl
{
    private static readonly string[] Palette =
    {
        "#FFFFFF", "#B8C0CC", "#7D8794", "#FF5A5A", "#FF8A3D", "#FFB454", "#FFE45C",
        "#B6FF5C", "#39FF7A", "#7CE38B", "#6FE0D0", "#5CC8FF", "#7FA6FF", "#A98BFF",
        "#C79BFF", "#FF6B9A", "#FF4FA3", "#FF8C6B", "#FFD0A8", "#D6FFE2", "#FFE9C2",
    };

    /// <summary>
    /// Applica un colore ("#RRGGBB", oppure null = quello del tema) e restituisce cosa mostrare dopo:
    /// il testo della casella del codice e il colore su cui portare i cursori.
    /// </summary>
    public delegate (string Text, Color? Color) ApplyColor(string? hex);

    private ApplyColor? _apply;
    private bool _syncingHsv;

    /// <summary>Si è premuto "Done": chi ospita il selettore chiude il suo Popup.</summary>
    public event Action? Done;

    // I cursori HSV cambiano il colore a ogni passo: applicarlo ogni volta (per il colore dell'app vuol dire
    // ridipingere tutta l'interfaccia) bloccava il trascinamento. Si applica al massimo ogni 60 ms, e l'ultimo
    // valore sempre (anche chiudendo il selettore subito dopo).
    private readonly System.Windows.Threading.DispatcherTimer _hsvTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private string? _pendingHex;

    public ColorPicker()
    {
        InitializeComponent();
        BuildPalette();
        _hsvTimer.Tick += (_, _) => FlushHsv();
    }

    private void FlushHsv()
    {
        _hsvTimer.Stop();
        string? hex = _pendingHex;
        _pendingHex = null;
        if (hex != null) _apply?.Invoke(hex);
    }

    /// <summary>Prepara il selettore per un nuovo bersaglio, senza che questo conti come una scelta.</summary>
    public void Begin(ApplyColor apply, string text, Color current)
    {
        FlushHsv();   // l'ultimo colore va a chi lo stava scegliendo, non al nuovo bersaglio
        _apply = apply;
        HexBox.Text = text;
        SyncHsvSliders(current);
    }

    /// <summary>Il selettore non sceglie più per nessuno (il Popup si è chiuso).</summary>
    public void End()
    {
        FlushHsv();
        _apply = null;
    }

    private void BuildPalette()
    {
        foreach (string hex in Palette)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = OverlayView.ParseBrush(hex),
                Tag = hex,
                Margin = new Thickness(0, 0, 6, 6),
                Width = 26,
                Height = 26,
                ToolTip = hex,
            };
            button.Click += (s, _) => PickColor((string)((Button)s).Tag);
            PalettePanel.Children.Add(button);
        }
    }

    /// <summary>Porta i tre cursori sul colore attuale, senza che questo conti come una scelta.</summary>
    private void SyncHsvSliders(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        _syncingHsv = true;
        HueSlider.Value = h;
        SatSlider.Value = max == 0 ? 0 : d / max * 100;
        ValSlider.Value = max * 100;
        _syncingHsv = false;
    }

    private void Hsv_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingHsv || _apply == null || !IsVisible) return;
        double h = HueSlider.Value % 360, s = SatSlider.Value / 100, v = ValSlider.Value / 100;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        string hex = $"#{(int)Math.Round((r + m) * 255):X2}{(int)Math.Round((g + m) * 255):X2}{(int)Math.Round((b + m) * 255):X2}";
        HexBox.Text = hex;
        _pendingHex = hex;
        if (!_hsvTimer.IsEnabled) _hsvTimer.Start();
    }

    private void ColorAuto_Click(object sender, RoutedEventArgs e) => PickColor(null);

    private void ColorDone_Click(object sender, RoutedEventArgs e)
    {
        FlushHsv();
        Done?.Invoke();
    }

    /// <summary>Applica un colore lasciando aperto il selettore: si chiude solo con "Done", così se ne provano diversi.</summary>
    private void PickColor(string? hex)
    {
        if (_apply == null) return;
        _pendingHex = null;   // una scelta esplicita vince sul passo del cursore non ancora applicato
        _hsvTimer.Stop();
        var (text, color) = _apply(hex);
        HexBox.Text = text;
        if (color is { } c) SyncHsvSliders(c);
    }

    private void HexOk_Click(object sender, RoutedEventArgs e)
    {
        string text = HexBox.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 7 && OverlayView.ParseBrush(text) != null) PickColor(text.ToUpperInvariant());
    }
}
