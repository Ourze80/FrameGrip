using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Pagina Opzioni (lingua, tema, colore, avvio con Windows, chiusura). Ogni scelta vale e si salva subito:
/// le impostazioni avvisano la finestra principale, che le salva e le applica.
/// </summary>
public partial class OptionsPage : UserControl
{
    private static readonly (string Hex, string Key)[] AccentChoices =
    {
        ("#4C9BFF", "Accent.Blue"), ("#3DD6D0", "Accent.Teal"), ("#5BD67A", "Accent.Green"), ("#B38CFF", "Accent.Purple"),
        ("#FF7AB6", "Accent.Pink"), ("#FF6B6B", "Accent.Red"), ("#FFB454", "Accent.Amber"), ("#E6EAF0", "Accent.White"),
    };

    private Settings _settings = null!;
    private LocText? _autostartError;

    /// <summary>Chiesto da "Rivedi la guida": la finestra principale riapre la guida rapida.</summary>
    public event Action? ReplayGuide;

    public OptionsPage()
    {
        InitializeComponent();
        // Il selettore resta aperto finché non si preme "Done"; si chiude da solo se si lascia la pagina o la finestra.
        Picker.Done += () => ColorPopup.IsOpen = false;
        ColorPopup.Closed += (_, _) => Picker.End();
        IsVisibleChanged += (_, _) =>
        {
            ColorPopup.IsOpen = false;
            if (IsVisible && _settings != null) { ReadAutostart(); ReadDisplay(); }   // lo stato vero si rilegge a ogni apertura
        };
        Loaded += (_, _) =>
        {
            if (Window.GetWindow(this) is not { } w) return;
            w.Deactivated += (_, _) => ColorPopup.IsOpen = false;
            w.LocationChanged += (_, _) => ColorPopup.IsOpen = false;
        };
    }

    /// <summary>Collega la pagina alle impostazioni: una volta sola, all'avvio della finestra principale.</summary>
    public void Attach(Settings settings)
    {
        _settings = settings;
        DataContext = settings;
        BuildAccentChoices();
        UpdateChoices();
        ReadDisplay();
        _settings.PropertyChanged += Settings_PropertyChanged;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    private void GuideButton_Click(object sender, RoutedEventArgs e) => ReplayGuide?.Invoke();

    private void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Settings.Language) or nameof(Settings.AppTheme)) UpdateChoices();
        if (e.PropertyName == nameof(Settings.AppAccent)) UpdateAccentChecks();
        if (e.PropertyName == nameof(Settings.VrrDisplay)) UpdateVrr();
    }

    // ------------------------------------------------------------ schermo

    private void ReadDisplay()
    {
        var d = DisplayInfo.Primary();
        DisplayName.Text = d.Name.Length > 0 ? d.Name : Loc.T("Display.Unknown");
        var parts = new List<string>();
        if (d.Width > 0) parts.Add($"{d.Width} × {d.Height}");
        if (d.Hz > 0) parts.Add($"{d.Hz} Hz");
        DisplayMode.Text = string.Join("  ·  ", parts);
        DisplayMode.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateVrr();
    }

    private void UpdateVrr()
    {
        VrrYes.IsChecked = _settings.VrrDisplay == true;
        VrrNo.IsChecked = _settings.VrrDisplay == false;
        VrrUnknown.IsChecked = _settings.VrrDisplay == null;
    }

    private void Vrr_Click(object sender, RoutedEventArgs e)
    {
        _settings.VrrDisplay = sender == VrrYes ? true : sender == VrrNo ? false : null;
        UpdateVrr();
    }

    /// <summary>Da "Cambia" sulla pagina di un gioco: la tastiera arriva sulla scelta attiva.</summary>
    public void FocusVrr() =>
        (_settings.VrrDisplay == true ? VrrYes : _settings.VrrDisplay == false ? VrrNo : VrrUnknown).Focus();

    private void OnLanguageChanged()
    {
        // I nomi dei colori sono costruiti nel codice: si rifanno nella lingua nuova.
        AccentPanel.Children.Clear();
        BuildAccentChoices();
        ShowAutostartError();
    }

    /// <summary>Accende la scelta attuale di lingua e tema (le due pillole di ogni riga si escludono).</summary>
    private void UpdateChoices()
    {
        LangEn.IsChecked = _settings.Language == "en";
        LangIt.IsChecked = _settings.Language == "it";
        ThemeDark.IsChecked = _settings.AppTheme == Theming.Dark;
        ThemeLight.IsChecked = _settings.AppTheme == Theming.Light;
    }

    private void LangEn_Click(object sender, RoutedEventArgs e) { _settings.Language = "en"; UpdateChoices(); }
    private void LangIt_Click(object sender, RoutedEventArgs e) { _settings.Language = "it"; UpdateChoices(); }
    private void ThemeDark_Click(object sender, RoutedEventArgs e) { _settings.AppTheme = Theming.Dark; UpdateChoices(); }
    private void ThemeLight_Click(object sender, RoutedEventArgs e) { _settings.AppTheme = Theming.Light; UpdateChoices(); }

    // ------------------------------------------------------------ colore dell'app

    private readonly List<(string Hex, Button Button)> _accentButtons = new();

    /// <summary>Segna con una spunta il colore in uso; con un colore personalizzato nessuno è spuntato.</summary>
    private void UpdateAccentChecks()
    {
        foreach (var (hex, button) in _accentButtons)
        {
            if (!string.Equals(hex, _settings.AppAccent, StringComparison.OrdinalIgnoreCase))
            {
                button.Content = null;
                continue;
            }
            var color = ColorOf(hex) ?? Colors.White;
            double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
            button.Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 3.5,8.5 L 7,12 L 13,5"),
                Stroke = luminance > 0.55 ? new SolidColorBrush(Color.FromRgb(0x12, 0x14, 0x18)) : Brushes.White,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
        }
    }

    private void BuildAccentChoices()
    {
        _accentButtons.Clear();
        foreach (var (hex, key) in AccentChoices)
        {
            var button = new Button
            {
                Style = (Style)FindResource("Swatch"),
                Background = OverlayView.ParseBrush(hex),
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 8),
                ToolTip = Loc.T(key),
            };
            System.Windows.Automation.AutomationProperties.SetName(button, Loc.T(key));
            button.Click += (_, _) => _settings.AppAccent = hex;
            AccentPanel.Children.Add(button);
            _accentButtons.Add((hex, button));
        }
        UpdateAccentChecks();

        // Qualsiasi altro colore: apre lo stesso selettore usato per le voci dell'overlay.
        var more = new Button
        {
            Style = (Style)FindResource("DarkButton"),
            Content = Loc.T("Options.MoreColors"),
            Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 8, 8), FontSize = 12,
            ToolTip = Loc.T("Options.MoreColorsTip"),
        };
        more.Click += (s, _) =>
        {
            Picker.Begin(hex =>
            {
                _settings.AppAccent = hex ?? Settings.DefaultAccent;
                return (_settings.AppAccent, ColorOf(_settings.AppAccent));
            }, _settings.AppAccent, ColorOf(_settings.AppAccent) ?? Colors.White);
            ColorPopup.PlacementTarget = (UIElement)s;
            ColorPopup.IsOpen = true;
        };
        AccentPanel.Children.Add(more);
    }

    private static Color? ColorOf(string hex) => (OverlayView.ParseBrush(hex) as SolidColorBrush)?.Color;

    // ------------------------------------------------------------ avvio con Windows

    /// <summary>Lo stato vero si rilegge a ogni apertura: l'attività pianificata può cambiare anche da fuori.</summary>
    private async void ReadAutostart()
    {
        AutostartToggle.IsEnabled = false;
        bool on = await Task.Run(Autostart.IsEnabled);
        AutostartToggle.IsChecked = on;
        AutostartToggle.IsEnabled = true;
    }

    private async void Autostart_Click(object sender, RoutedEventArgs e)
    {
        bool want = AutostartToggle.IsChecked == true;
        AutostartToggle.IsEnabled = false;
        LocText? error = await Task.Run(() => Autostart.Set(want));
        bool now = await Task.Run(Autostart.IsEnabled);
        AutostartToggle.IsChecked = now;
        AutostartToggle.IsEnabled = true;
        _autostartError = error;
        ShowAutostartError();
    }

    private void ShowAutostartError()
    {
        AutostartStatus.Text = _autostartError?.ToString() ?? "";
        AutostartStatus.Visibility = _autostartError == null ? Visibility.Collapsed : Visibility.Visible;
    }
}
