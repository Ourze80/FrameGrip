using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Tema della finestra dell'app (scuro o chiaro) e colore d'accento. Non riguarda l'overlay:
/// quello ha i suoi temi (<see cref="Theme"/>) e resta uguale qualunque tema abbia l'app.
/// I colori neutri stanno in Themes\Dark.xaml e Themes\Light.xaml: si scambia il dizionario
/// e tutto ciò che li usa con DynamicResource si aggiorna da solo.
/// </summary>
public static class Theming
{
    public const string Dark = "dark", Light = "light";

    public static bool IsLight { get; private set; }

    /// <summary>Dopo ogni cambio di tema, per le parti colorate dal codice.</summary>
    public static event Action? Changed;

    public static string Normalize(string? theme) => theme == Light ? Light : Dark;

    /// <summary>Mette il tema scelto e ricalcola l'accento (il velo dell'accento cambia col tema).</summary>
    public static void Apply(string? theme, string accent)
    {
        bool light = Normalize(theme) == Light;
        var app = Application.Current;
        if (app == null) return;

        var dicts = app.Resources.MergedDictionaries;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Absolute),
        };
        // Il dizionario del tema è sempre il primo di quelli uniti in App.xaml.
        if (dicts.Count > 0) dicts[0] = dict; else dicts.Add(dict);
        IsLight = light;

        ApplyAccent(accent);
        foreach (Window w in app.Windows) ApplyTitleBar(w);
        Changed?.Invoke();
    }

    // Cambia il colore d'accento di tutte le finestre: pieno, velato (per gli sfondi delle scelte attive),
    // il colore del testo che ci sta sopra (scuro o chiaro secondo la luminosità) e quello delle scritte
    // in accento, che sul bianco diventano del colore del testo normale per restare leggibili.
    public static void ApplyAccent(string hex)
    {
        var app = Application.Current;
        if (app == null) return;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); }
        catch { c = (Color)ColorConverter.ConvertFromString(Settings.DefaultAccent); }
        c.A = 255;
        double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        var resources = app.Resources;
        var accent = Frozen(new SolidColorBrush(c));
        resources["Accent"] = accent;
        resources["AccentSoft"] = Frozen(new SolidColorBrush(Color.FromArgb(IsLight ? (byte)0x24 : (byte)0x26, c.R, c.G, c.B)));
        resources["OnAccent"] = Frozen(new SolidColorBrush(luminance > 0.55 ? Color.FromRgb(0x12, 0x14, 0x18) : Colors.White));
        resources["AccentText"] = IsLight && resources["Fg"] is Brush fg ? fg : accent;
    }

    /// <summary>Barra del titolo dello stesso colore della finestra, scura o chiara secondo il tema.</summary>
    public static void ApplyTitleBar(Window w)
    {
        // L'overlay e le altre finestre senza bordo non hanno barra del titolo.
        if (w is not (MainWindow or CloseDialog)) return;
        IntPtr hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        Color bg = ColorOf("Bg", Color.FromRgb(0x0A, 0x0B, 0x0D));
        Color fg = ColorOf("Fg", Color.FromRgb(0xF2, 0xF4, 0xF7));
        Native.TitleBar(hwnd, !IsLight, bg.R, bg.G, bg.B, fg.R, fg.G, fg.B);
    }

    private static Color ColorOf(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush b ? b.Color : fallback;

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
