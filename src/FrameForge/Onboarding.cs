using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// La guida della prima apertura: tre passi (stile, cosa vedere, gioca) sopra la pagina Giochi oscurata.
/// Ogni scelta si applica subito, quindi "Salta" lascia quello che si è già scelto.
/// Si rivede da "Guida rapida" nella barra a sinistra o dalle Opzioni.
/// </summary>
public sealed class Onboarding : Grid
{
    private static readonly string[] LayoutKeys = { "Layout.List", "Layout.Bar", "Layout.Grid", "Layout.Console", "Layout.Dashboard", "Layout.Rings", "Layout.Icons" };
    private static readonly string[] PresetKeys = { "Sns.Essential", "Sns.Gaming", "Sns.Full" };

    private readonly Settings _s;
    private readonly SensorsCurated _curated;
    private readonly Func<int> _found;
    private readonly ContentControl _body = new() { Focusable = false };
    private readonly TextBlock _stepText = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back, _next, _skip;
    private readonly TextBlock _welcome;
    private TextBlock? _foundText;
    private int _step;

    /// <summary>L'anteprima dell'overlay nel primo passo: la riempie la finestra, come quella della pagina Overlay.</summary>
    public OverlayView Preview { get; }

    public event Action? Finished;

    private readonly Func<int, UIElement?> _thumb;

    public Onboarding(Settings s, SensorsCurated curated, Func<int> gamesFound, Func<int, UIElement?> thumb)
    {
        _s = s;
        _thumb = thumb;
        _curated = curated;
        _found = gamesFound;
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(0xB8, 0x05, 0x06, 0x09));
        Preview = new OverlayView { Settings = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

        var card = new Border
        {
            Width = 660, CornerRadius = new CornerRadius(18), Padding = new Thickness(30, 26, 30, 22), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 40, ShadowDepth = 0, Opacity = 0.5 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "LineStrong");

        var root = new DockPanel();
        // testa: "Benvenuto" e a che passo si è
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
        DockPanel.SetDock(head, Dock.Top);
        _stepText.FontSize = 12;
        _stepText.VerticalAlignment = VerticalAlignment.Center;
        _stepText.Margin = new Thickness(10, 0, 0, 0);
        _stepText.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_dots);
        right.Children.Add(_stepText);
        DockPanel.SetDock(right, Dock.Right);
        head.Children.Add(right);
        _welcome = Text(Loc.T("Guide.Welcome"), 12.5, "AccentText", true);
        head.Children.Add(_welcome);
        root.Children.Add(head);

        // piede: Salta a sinistra, Indietro / Avanti a destra
        var foot = new DockPanel { Margin = new Thickness(0, 22, 0, 0) };
        DockPanel.SetDock(foot, Dock.Bottom);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        _back = new Button { Content = Loc.T("Guide.Back"), Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(16, 8, 16, 8) };
        _back.SetResourceReference(StyleProperty, "DarkButton");
        _back.Click += (_, _) => Go(_step - 1);
        _next = new Button { MinWidth = 110 };
        _next.SetResourceReference(StyleProperty, "AccentButton");
        _next.Click += (_, _) => { if (_step == 2) Finish(); else Go(_step + 1); };
        buttons.Children.Add(_back);
        buttons.Children.Add(_next);
        foot.Children.Add(buttons);
        var skip = _skip = new Button { Content = Loc.T("Guide.Skip"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 6, 0, 6) };
        skip.SetResourceReference(StyleProperty, "LinkButton");
        skip.SetResourceReference(Control.ForegroundProperty, "FgDim");
        skip.Click += (_, _) => Finish();
        foot.Children.Add(skip);
        root.Children.Add(foot);

        root.Children.Add(_body);
        card.Child = root;
        Children.Add(card);

        // Esc chiude come "Salta"
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Finish(); e.Handled = true; } };
    }

    /// <summary>Cambio di lingua: si riscrivono i testi (e il passo aperto).</summary>
    public void Relocalize()
    {
        _welcome.Text = Loc.T("Guide.Welcome");
        _back.Content = Loc.T("Guide.Back");
        _skip.Content = Loc.T("Guide.Skip");
        if (IsVisible) Go(_step);
    }

    public void Show()
    {
        Visibility = Visibility.Visible;
        Go(0);
    }

    private void Finish()
    {
        Visibility = Visibility.Collapsed;
        _s.GuideDone = true;
        Finished?.Invoke();
    }

    /// <summary>Il numero dei giochi trovati cambia mentre la libreria si legge ancora: si aggiorna dal timer.</summary>
    public void UpdateFound()
    {
        if (_foundText != null) _foundText.Text = Loc.F("Guide.Found", _found());
    }

    private void Go(int step)
    {
        _step = Math.Clamp(step, 0, 2);
        _foundText = null;
        _body.Content = _step switch { 0 => StyleStep(), 1 => PresetStep(), _ => PlayStep() };
        _stepText.Text = Loc.F("Guide.Step", _step + 1, 3);
        _dots.Children.Clear();
        for (int i = 0; i < 3; i++)
        {
            var d = new Border { Width = i == _step ? 18 : 7, Height = 7, CornerRadius = new CornerRadius(3.5), Margin = new Thickness(3, 0, 0, 0) };
            d.SetResourceReference(Border.BackgroundProperty, i == _step ? "Accent" : "LineStrong");
            _dots.Children.Add(d);
        }
        _back.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = Loc.T(_step == 2 ? "Guide.Start" : "Guide.Next");
        Dispatcher.BeginInvoke(() => _next.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    // ------------------------------------------------------------ i tre passi

    private UIElement StyleStep()
    {
        var p = Intro("Guide.StyleTitle", "Guide.StyleSub");
        // a sinistra gli stili uno sotto l'altro (miniatura + nome), a destra l'anteprima verticale
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = new StackPanel();
        KeyboardNavigation.SetDirectionalNavigation(list, KeyboardNavigationMode.Cycle);
        var cards = new List<CheckBox>();
        for (int i = 0; i < LayoutKeys.Length; i++)
        {
            int index = i;
            var row = new DockPanel();
            var thumbBox = new Border { Width = 64, Height = 36, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            if (_thumb(i) is { } th) thumbBox.Child = new Viewbox { Child = th, Stretch = Stretch.Uniform };
            row.Children.Add(thumbBox);
            row.Children.Add(Text(Loc.T(LayoutKeys[i]), 13.5, "Fg", true));
            var c = Choice(row, new Thickness(12, 10, 12, 10));
            AutomationProperties.SetName(c, Loc.T(LayoutKeys[i]));
            c.Margin = new Thickness(0, 0, 0, 6);
            c.IsChecked = _s.Layout == i;
            c.Click += (_, _) =>
            {
                _s.Layout = index;
                foreach (var o in cards) o.IsChecked = o == c;
            };
            cards.Add(c);
            list.Children.Add(c);
        }
        grid.Children.Add(list);

        // anteprima: lo stesso overlay che si vedrà nei giochi, su uno schermo di gioco finto e verticale
        var box = new PreviewScene(backdropChoice: false);
        box.Show(Preview);
        // alta quanto l'elenco degli stili: l'anteprima si adatta al riquadro, non lo allunga
        box.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualHeight)) { Source = list });
        Grid.SetColumn(box, 2);
        grid.Children.Add(box);
        p.Children.Add(grid);
        return p;
    }

    private UIElement PresetStep()
    {
        var p = Intro("Guide.PresetTitle", "Guide.PresetSub");
        var grid = new UniformGrid { Columns = 3 };
        int current = _curated.CurrentPreset();
        var cards = new List<CheckBox>();
        string[] glyphs = { "", "", "" };
        for (int i = 0; i < PresetKeys.Length; i++)
        {
            int index = i;
            var content = new StackPanel();
            var icon = Text(glyphs[i], 20, "AccentText");
            icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            content.Children.Add(icon);
            content.Children.Add(Spaced(Text(Loc.T(PresetKeys[i]), 14.5, "Fg", true), 0, 12, 0, 4));
            var d = Text(Loc.T(PresetKeys[i] + "Desc"), 12, "FgDim");
            d.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(d);
            var c = Choice(content, new Thickness(16, 16, 16, 16));
            AutomationProperties.SetName(c, Loc.T(PresetKeys[i]));
            c.Margin = new Thickness(0, 0, i < 2 ? 10 : 0, 0);
            c.VerticalContentAlignment = VerticalAlignment.Stretch;
            c.IsChecked = current == i;
            c.Click += (_, _) =>
            {
                _curated.ApplyPreset(index);
                foreach (var o in cards) o.IsChecked = o == c;
            };
            cards.Add(c);
            grid.Children.Add(c);
        }
        p.Children.Add(grid);
        p.Children.Add(Spaced(Wrap(Loc.T("Guide.PresetNote"), 12, "FgDim"), 0, 14, 0, 0));
        return p;
    }

    private UIElement PlayStep()
    {
        var p = Intro("Guide.PlayTitle", "Guide.PlaySub");
        var found = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 12) };
        found.SetResourceReference(Border.BackgroundProperty, "Control");
        var fp = new DockPanel();
        var icon = Text("", 20, "AccentText");
        icon.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        icon.Margin = new Thickness(0, 0, 14, 0);
        fp.Children.Add(icon);
        var col = new StackPanel();
        _foundText = Text("", 15, "Fg", true);
        col.Children.Add(_foundText);
        col.Children.Add(Spaced(Wrap(Loc.T("Guide.FoundSub"), 12, "FgDim"), 0, 3, 0, 0));
        fp.Children.Add(col);
        found.Child = fp;
        p.Children.Add(found);
        UpdateFound();

        var keyRow = new DockPanel();
        var keycap = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        keycap.SetResourceReference(Border.BackgroundProperty, "Control");
        keycap.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
        keycap.Child = Text(_s.HotkeyName, 12, "Fg", true);
        keyRow.Children.Add(keycap);
        keyRow.Children.Add(Wrap(Loc.T("Guide.Key"), 12.5, "FgDim"));
        p.Children.Add(keyRow);
        return p;
    }

    // ------------------------------------------------------------ mattoncini

    private static StackPanel Intro(string title, string sub)
    {
        var p = new StackPanel();
        p.Children.Add(Text(Loc.T(title), 22, "Fg", true));
        p.Children.Add(Spaced(Wrap(Loc.T(sub), 13, "FgDim"), 0, 6, 0, 18));
        return p;
    }

    private static CheckBox Choice(object content, Thickness padding)
    {
        var c = new CheckBox { Content = content, Padding = padding };
        c.SetResourceReference(StyleProperty, "ChoiceCard");
        if (content is TextBlock t) AutomationProperties.SetName(c, t.Text);
        return c;
    }

    private static T Spaced<T>(T e, double l, double t, double r, double b) where T : FrameworkElement
    {
        e.Margin = new Thickness(l, t, r, b);
        return e;
    }

    private static TextBlock Wrap(string text, double size, string brush)
    {
        var t = Text(text, size, brush);
        t.TextWrapping = TextWrapping.Wrap;
        return t;
    }

    private static TextBlock Text(string text, double size, string brush, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }
}
