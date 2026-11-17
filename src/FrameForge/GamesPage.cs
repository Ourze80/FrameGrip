using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Pagina Giochi: l'elenco dei giochi (trovati nei negozi, con un profilo o già giocati) e la pagina di ciascuno.
/// In cima il gioco aperto adesso, con FPS e limite dal vivo. Le copertine vengono solo da file già sul disco
/// (cache di Steam, logo di Game Pass): niente download; senza immagine si disegna una copertina con le iniziali.
/// </summary>
public sealed class GamesPage : Grid
{
    private readonly Settings _s;
    private readonly IdealMeasure _ideal;
    private readonly Func<GameInfo?> _game;
    private readonly Func<FrameSnapshot?> _frames;
    private readonly Func<string?> _api;

    public FpsPage Detail { get; }
    /// <summary>Ricerca e "Aggiungi un gioco": la finestra li mette nella riga del titolo della pagina.</summary>
    public FrameworkElement HeaderTools { get; }
    public bool InDetail => _detailItem != null;
    public event Action? ViewChanged;

    private readonly ScrollViewer _homeScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly ScrollViewer _detailScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _home = new() { Margin = new Thickness(0, 0, 12, 0) };
    private readonly StackPanel _detailHead = new();
    private readonly StackPanel _heroBox = new(), _filterBox = new(), _allEditBox = new();
    private readonly UniformGrid _grid = new() { Columns = 4 };
    private readonly TextBlock _emptyText = new() { FontSize = 13, Margin = new Thickness(2, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _search = new() { Width = 250, Height = 36, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(30, 0, 8, 0) };

    private List<GameItem> _items = new();
    private GameItem? _detailItem;
    private string _filter = "";
    private bool _editAll;
    private string _sig = "";
    private TextBlock? _heroFps, _heroLimit, _heroScope, _heroSmooth, _heroWorst;

    public GamesPage(Settings s, IdealMeasure ideal, Func<GameInfo?> game, Func<FrameSnapshot?> frames, Func<string?> api)
    {
        _s = s; _ideal = ideal; _game = game; _frames = frames; _api = api;
        Detail = new FpsPage(s, ideal, game);
        Detail.OpenGameRequested += g => { Rebuild(); if (_items.FirstOrDefault(i => i.Matches(g)) is { } it) Open(it); };

        _home.Children.Add(_heroBox);
        _home.Children.Add(_filterBox);
        _home.Children.Add(_allEditBox);
        _home.Children.Add(_grid);
        _home.Children.Add(_emptyText);
        _emptyText.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _homeScroll.Content = _home;
        _homeScroll.SizeChanged += (_, _) => FitColumns();

        var detail = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        detail.Children.Add(_detailHead);
        detail.Children.Add(Detail);
        _detailScroll.Content = detail;
        Children.Add(_homeScroll);
        Children.Add(_detailScroll);

        HeaderTools = BuildHeaderTools();

        Loc.LanguageChanged += () => { _sig = ""; Refresh(); if (_detailItem != null) BuildDetailHead(); };
        _s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Settings.LimiterFps) or nameof(Settings.LimiterEnabled) or nameof(Settings.GameProfiles))
                Dispatcher.BeginInvoke(() => { _sig = ""; Refresh(); });
        };
        Rebuild();
        BuildHome();
    }

    // ------------------------------------------------------------ mattoncini

    private static TextBlock T(string text, double size = 12, string brush = "Fg", bool bold = false, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (!wrap) t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private static TextBlock Icon(int glyph, double size, string brush)
    {
        var t = new TextBlock { Text = char.ConvertFromUtf32(glyph), FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static Border Pill(string text, string fg = "FgDim", string bg = "ControlHover", double size = 9.5)
    {
        var b = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1.5, 6, 2), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 6, 0) };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        var t = T(text, size, fg, true);
        b.Child = t;
        return b;
    }

    private static Button Btn(string text, bool accent, RoutedEventHandler click, double height = 38)
    {
        var b = new Button { Content = text, Height = height, FontSize = 13, Padding = new Thickness(16, 0, 16, 0) };
        b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "DarkButton");
        b.Click += click;
        return b;
    }

    private static string Fmt(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Copertina: immagine del negozio se è sul disco, altrimenti colori fissi del gioco e iniziali.</summary>
    private static FrameworkElement Cover(GameItem g, double height, double radius, double initials)
    {
        var (a, b) = GameCatalog.FallbackColors(g.Name);
        var box = new Grid { Height = height, ClipToBounds = true };
        var fallback = new Border
        {
            CornerRadius = new CornerRadius(radius, radius, 0, 0),
            Background = new LinearGradientBrush(a, b, 35),
        };
        fallback.Child = new TextBlock
        {
            Text = Limits.Initials(g.Name), FontSize = initials, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromArgb(0xE6, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        box.Children.Add(fallback);
        var img = new Image { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        box.Children.Add(img);
        // si carica quando il riquadro compare: la prima volta in un altro thread, poi dalla memoria
        box.Loaded += async (_, _) =>
        {
            if (img.Source != null) return;
            var src = await GameCatalog.CoverAsync(g);
            if (src != null) { img.Source = src; fallback.Visibility = Visibility.Collapsed; }
        };
        if (radius > 0)
            box.SizeChanged += (_, e) => box.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height + radius), radius, radius);
        return box;
    }

    private static string When(DateTime? d)
    {
        if (d is not { } t || t == default) return Loc.T("Home.Never");
        int days = (DateTime.Today - t.Date).Days;
        return days <= 0 ? Loc.T("Home.Today") : days == 1 ? Loc.T("Home.Yesterday") : days < 7 ? Loc.F("Home.DaysAgo", days)
            : days < 14 ? Loc.T("Home.LastWeek")
            : t.ToString(Loc.IsItalian ? "d MMM" : "MMM d", Loc.IsItalian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo("en-US"));
    }

    private static string MeasureCount(int n) => n == 0 ? Loc.T("Home.Measures0") : n == 1 ? Loc.T("Home.Measures1") : Loc.F("Home.MeasuresN", n);

    // ------------------------------------------------------------ dati

    private void Rebuild()
    {
        _items = GameCatalog.Build(_s, _game());
        // il gioco aperto prima, poi quelli giocati di recente, poi in ordine di nome
        _items = _items.OrderByDescending(i => i.Running)
            .ThenByDescending(i => i.LastSeen ?? DateTime.MinValue)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (_detailItem != null)
        {
            var again = _items.FirstOrDefault(i => string.Equals(i.Key, _detailItem.Key, StringComparison.OrdinalIgnoreCase))
                ?? _items.FirstOrDefault(i => i.Exes.Overlaps(_detailItem.Exes));
            if (again != null) { _detailItem = again; Detail.SetContext(again); }
        }
    }

    /// <summary>Una volta al secondo: rifà l'elenco se è cambiato qualcosa, aggiorna i numeri dal vivo.</summary>
    public void Refresh()
    {
        var g = _game();
        string sig = $"{g?.Exe}|{GameLibrary.Games.Count}|{_s.GameProfiles.Count}|{string.Join(",", _s.GameProfiles.Select(p => p.Key + p.Value.Fps))}|{_s.GameHistory.Count}|{MeasureStore.All.Count}|{_s.LimiterFps}|{_s.LimiterEnabled}|{Loc.IsItalian}|{GameLibrary.Ready}";
        if (sig != _sig)
        {
            bool gameChanged = _sig.Split('|')[0] != (g?.Exe ?? "");
            _sig = sig;
            Rebuild();
            BuildHome();
            if (_detailItem != null && gameChanged) BuildDetailHead();
        }
        UpdateHero();
        if (_detailItem != null) Detail.Refresh();
    }

    public void LibraryChanged() { _sig = ""; Refresh(); }

    // ------------------------------------------------------------ elenco

    private FrameworkElement BuildHeaderTools()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        var box = new Grid();
        _search.SetResourceReference(StyleProperty, "DarkBox");
        AutomationProperties.SetName(_search, Loc.T("Home.Search"));
        _search.TextChanged += (_, _) => BuildGrid();
        box.Children.Add(_search);
        var ic = Icon(0xE721, 13, "FgDim"); ic.HorizontalAlignment = HorizontalAlignment.Left; ic.Margin = new Thickness(11, 0, 0, 0); ic.IsHitTestVisible = false;
        box.Children.Add(ic);
        var hint = T(Loc.T("Home.Search"), 13, "FgDim"); hint.Margin = new Thickness(31, 0, 0, 0); hint.IsHitTestVisible = false;
        _search.TextChanged += (_, _) => hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Loc.LanguageChanged += () => { hint.Text = Loc.T("Home.Search"); AutomationProperties.SetName(_search, Loc.T("Home.Search")); };
        box.Children.Add(hint);
        row.Children.Add(box);
        var add = new Button { Height = 36, Padding = new Thickness(14, 0, 16, 0), Margin = new Thickness(10, 0, 0, 0) };
        add.SetResourceReference(StyleProperty, "DarkButton");
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        var plus = Icon(0xE710, 11, "Fg"); plus.Margin = new Thickness(0, 0, 8, 0);
        addRow.Children.Add(plus);
        var addText = T(Loc.T("Home.Add"), 13);
        Loc.LanguageChanged += () => addText.Text = Loc.T("Home.Add");
        addRow.Children.Add(addText);
        add.Content = addRow;
        add.ToolTip = Loc.T("Home.AddTip");
        add.Click += (_, _) => AddGame();
        row.Children.Add(add);
        return row;
    }

    private void BuildHome()
    {
        BuildHero();
        BuildFilters();
        BuildAllEditor();
        BuildGrid();
    }

    private void BuildHero()
    {
        _heroBox.Children.Clear();
        _heroFps = _heroLimit = _heroScope = _heroSmooth = _heroWorst = null;
        var g = _game();
        var it = g != null ? _items.FirstOrDefault(i => i.Running) : null;
        if (g == null || it == null) return;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = Cover(it, 158, 0, 44);
        cover.Margin = new Thickness(0);
        var coverBox = new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = cover, Margin = new Thickness(0, 0, 22, 0) };
        coverBox.SizeChanged += (_, e) => coverBox.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width - 22, e.NewSize.Height), 10, 10);
        grid.Children.Add(coverBox);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Pill("●  " + Loc.T("Game.Running").ToUpperInvariant(), "Good", "GoodSoft"));
        var name = T(it.Name, 24, "Fg", true); name.Margin = new Thickness(0, 6, 0, 6); name.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        info.Children.Add(name);
        info.Children.Add(Tags(it, g));
        var stats = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        StackPanel Stat(string label, out TextBlock value, out TextBlock sub)
        {
            var c = new StackPanel { Margin = new Thickness(0, 0, 34, 0) };
            c.Children.Add(T(label, 11, "FgDim"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
            value = T("—", 20, "Fg", true);
            sub = T("", 11, "FgDim"); sub.Margin = new Thickness(8, 6, 0, 0);
            row.Children.Add(value); row.Children.Add(sub);
            c.Children.Add(row);
            stats.Children.Add(c);
            return c;
        }
        Stat(Loc.T("Home.FpsNow"), out _heroFps, out _);
        Stat(Loc.T("Home.Limit"), out _heroLimit, out _heroScope);
        var smooth = Stat(Loc.T("Home.Smooth"), out _heroSmooth, out _heroWorst);
        smooth.ToolTip = Loc.T("Metric.smooth");
        info.Children.Add(stats);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Width = 230, Margin = new Thickness(18, 0, 0, 0) };
        var open = Btn(Loc.T("Home.OpenLimit"), true, (_, _) => Open(it), 40);
        actions.Children.Add(open);
        var find = Btn(Loc.T("Ideal.Title"), false, (_, _) => { Open(it); if (_ideal.State == IdealState.Idle) _ideal.OpenSetup(); }, 40);
        find.Margin = new Thickness(0, 10, 0, 0);
        actions.Children.Add(find);
        if (it.Measures().FirstOrDefault() is { } last)
        {
            var lm = T(Loc.F("Home.LastMeasure", When(last.Date), last.Recommended), 11, "FgDim");
            lm.HorizontalAlignment = HorizontalAlignment.Center; lm.Margin = new Thickness(0, 10, 0, 0);
            actions.Children.Add(lm);
        }
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1.5), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 18), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Good");
        _heroBox.Children.Add(card);
        UpdateHero();
    }

    /// <summary>Negozio, API grafica, modo dello schermo e frequenza: le etichette sotto il nome.</summary>
    private StackPanel Tags(GameItem it, GameInfo? g)
    {
        var tags = new StackPanel { Orientation = Orientation.Horizontal };
        if (it.Store != null) tags.Children.Add(Pill(it.Store.ToUpperInvariant(), "AccentText", "AccentSoft"));
        if (g != null)
        {
            if (_api() is { Length: > 0 } api) tags.Children.Add(Pill(api));
            if (g.Mode != WindowMode.Unknown)
                tags.Children.Add(Pill(Loc.T(g.Mode == WindowMode.Fullscreen ? "Fps.ModeFull" : g.Mode == WindowMode.Borderless ? "Fps.ModeBorderless" : "Fps.ModeWindowed")));
            if (g.RefreshHz > 0) tags.Children.Add(Pill(Loc.F("Home.ScreenHz", g.RefreshHz) + (_s.VrrDisplay == true ? " · VRR" : "")));
        }
        return tags;
    }

    private void UpdateHero()
    {
        if (_heroFps == null) return;
        var f = _frames();
        _heroFps.Text = f != null && f.Fps > 0 ? Fmt(f.Fps) : "—";
        var (fps, own) = Limits.Effective(_s, _game());
        bool on = _s.LimiterEnabled && !_ideal.SuspendsLimiter;
        _heroLimit!.Text = on ? fps.ToString(CultureInfo.InvariantCulture) : Loc.T("Home.Off");
        _heroScope!.Text = on ? Loc.T(own ? "Status.LimitGame" : "Status.LimitAll") : "";
        if (f != null && f.Fps > 0)
        {
            string key = f.PacingPct >= 90 ? "Stab.Excellent" : f.PacingPct >= 75 ? "Stab.Good" : f.PacingPct >= 55 ? "Stab.Fair" : "Stab.Poor";
            _heroSmooth!.Text = Loc.T(key);
            _heroSmooth.SetResourceReference(TextBlock.ForegroundProperty, f.PacingPct >= 75 ? "Good" : "Warn");
            _heroWorst!.Text = f.Low1Fps > 0 ? Loc.F("Home.Worst", Fmt(f.Low1Fps)) : "";
        }
        else { _heroSmooth!.Text = "—"; _heroWorst!.Text = ""; }
    }

    private static readonly string[] StoreOrder = { GameLibrary.Steam, GameLibrary.GamePass, GameLibrary.Gog, GameLibrary.Epic };

    private void BuildFilters()
    {
        _filterBox.Children.Clear();
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var sp = Icon(0xEC4A, 13, "AccentText"); sp.Margin = new Thickness(0, 0, 8, 0);
        right.Children.Add(sp);
        right.Children.Add(T(Loc.T("Home.AllOthers") + " ", 12.5, "FgDim"));
        right.Children.Add(T(_s.LimiterEnabled ? $"{_s.LimiterFps} FPS" : Loc.T("Home.Off"), 12.5, "Fg", true));
        var change = new Button { Content = _editAll ? Loc.T("Prof.Done") : Loc.T("Home.Change"), Height = 30, FontSize = 12.5, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(12, 0, 0, 0) };
        change.SetResourceReference(StyleProperty, "DarkButton");
        AutomationProperties.SetName(change, Loc.T("Home.ChangeTip"));
        change.ToolTip = Loc.T("Home.ChangeTip");
        change.Click += (_, _) => { _editAll = !_editAll; BuildFilters(); BuildAllEditor(); };
        right.Children.Add(change);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);

        var chips = new WrapPanel();
        void Chip(string id, string label, int n)
        {
            var c = new CheckBox { Content = $"{label}  {n}", IsChecked = _filter == id, Margin = new Thickness(0, 0, 8, 6) };
            c.SetResourceReference(StyleProperty, "Chip");
            c.Click += (_, _) => { _filter = id; BuildFilters(); BuildGrid(); };
            chips.Children.Add(c);
        }
        Chip("", Loc.T("Home.All"), _items.Count);
        foreach (var st in StoreOrder)
        {
            int n = _items.Count(i => i.Store == st);
            if (n > 0) Chip(st, st, n);
        }
        int other = _items.Count(i => i.Store == null || !StoreOrder.Contains(i.Store));
        if (other > 0) Chip("-", Loc.T("Home.Other"), other);
        row.Children.Add(chips);
        _filterBox.Children.Add(row);
    }

    /// <summary>"Cambia" il limite di tutti gli altri giochi: interruttore, cursore e scelte rapide, sotto la riga.</summary>
    private void BuildAllEditor()
    {
        _allEditBox.Children.Clear();
        if (!_editAll) return;
        var p = new StackPanel();
        var toggle = new CheckBox { Content = Loc.T("Game.LimitOn") };
        toggle.SetResourceReference(StyleProperty, "Toggle");
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Settings.LimiterEnabled)) { Source = _s, Mode = BindingMode.TwoWay });
        var val = T("", 22, "Fg", true);
        val.SetBinding(TextBlock.TextProperty, new Binding(nameof(Settings.LimiterFps)) { Source = _s });
        var head = new DockPanel();
        DockPanel.SetDock(val, Dock.Right);
        head.Children.Add(val);
        head.Children.Add(toggle);
        p.Children.Add(head);
        var slider = new Slider { Minimum = 20, Maximum = 360, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 10) };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(nameof(Settings.LimiterFps)) { Source = _s, Mode = BindingMode.TwoWay });
        AutomationProperties.SetName(slider, Loc.T("Home.AllOthers"));
        p.Children.Add(slider);
        var presets = new UniformGrid { Rows = 1 };
        foreach (int v in new[] { 30, 60, 90, 120, 144, 165, 240 })
        {
            var b = new Button { Content = v.ToString(CultureInfo.InvariantCulture), Height = 30, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "DarkButton");
            int fps = v;
            b.Click += (_, _) => _s.LimiterFps = fps;
            presets.Children.Add(b);
        }
        p.Children.Add(presets);
        var note = T(Loc.T("Home.AllOthersNote"), 11.5, "FgDim", wrap: true);
        note.Margin = new Thickness(0, 10, 0, 0);
        p.Children.Add(note);
        var card = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 16), Child = p, MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Right };
        card.SetResourceReference(Border.BackgroundProperty, "Panel");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        _allEditBox.Children.Add(card);
    }

    private void BuildGrid()
    {
        _grid.Children.Clear();
        string q = _search.Text.Trim();
        var shown = _items.Where(i => _filter == "" || (_filter == "-" ? i.Store == null || !StoreOrder.Contains(i.Store) : i.Store == _filter))
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var it in shown) _grid.Children.Add(Card(it));
        _emptyText.Text = shown.Count > 0 ? "" : q.Length > 0 ? Loc.T("Prof.NoMatch") : !GameLibrary.Ready ? Loc.T("Prof.Scanning") : Loc.T("Home.Empty");
        _emptyText.Visibility = shown.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        FitColumns();
    }

    /// <summary>Quattro colonne sulla finestra grande, meno quando si stringe.</summary>
    private void FitColumns()
    {
        double w = _homeScroll.ActualWidth;
        if (w <= 0) return;
        int cols = w >= 1080 ? 4 : w >= 800 ? 3 : w >= 520 ? 2 : 1;
        if (_grid.Columns != cols) _grid.Columns = cols;
    }

    private Button Card(GameItem it)
    {
        var p = new StackPanel();
        var top = new Grid();
        top.Children.Add(Cover(it, 128, 12, 34));
        if (it.Running)
        {
            var run = Pill("●  " + Loc.T("Home.RunningShort").ToUpperInvariant(), "Good", "GoodSoft");
            run.HorizontalAlignment = HorizontalAlignment.Left; run.VerticalAlignment = VerticalAlignment.Top; run.Margin = new Thickness(10, 10, 0, 0);
            top.Children.Add(run);
        }
        p.Children.Add(top);
        var body = new StackPanel { Margin = new Thickness(14, 10, 14, 12) };
        body.Children.Add(T(it.Name, 14, "Fg", true));
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        if (it.Store != null) meta.Children.Add(Pill(it.Store.ToUpperInvariant(), "AccentText", "AccentSoft", 9));
        int n = it.Measures().Count;
        meta.Children.Add(T(it.LastSeen == null && n == 0 ? Loc.T("Home.Never") : $"{MeasureCount(n)}  ·  {When(it.LastSeen)}", 11, "FgDim"));
        body.Children.Add(meta);
        var lim = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var prof = it.Profile(_s);
        if (!_s.LimiterEnabled) lim.Children.Add(T(Loc.T("Home.NoLimit"), 11.5, "FgDim"));
        else if (prof != null)
        {
            var ic = Icon(0xEC4A, 11, "AccentText"); ic.Margin = new Thickness(0, 0, 6, 0);
            lim.Children.Add(ic);
            lim.Children.Add(T($"{prof.Value.Profile.Fps} FPS", 12, "Fg", true));
            var sc = T(Loc.T("Status.LimitGame"), 11, "FgDim"); sc.Margin = new Thickness(6, 0, 0, 0);
            lim.Children.Add(sc);
        }
        else
        {
            var ic = Icon(0xEC4A, 11, "FgDim"); ic.Margin = new Thickness(0, 0, 6, 0);
            lim.Children.Add(ic);
            lim.Children.Add(T(Loc.F("Home.GeneralLimit", _s.LimiterFps), 11.5, "FgDim"));
        }
        body.Children.Add(lim);
        p.Children.Add(body);
        var b = new Button { Content = p, Margin = new Thickness(0, 0, 14, 14), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        b.SetResourceReference(StyleProperty, "GameCard");
        AutomationProperties.SetName(b, it.Name);
        b.Click += (_, _) => Open(it);
        return b;
    }

    /// <summary>"Aggiungi un gioco" per quelli che FrameGrip non trova da solo: si sceglie l'eseguibile.</summary>
    private void AddGame()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Loc.T("Home.AddFilter") + " (*.exe)|*.exe", Title = Loc.T("Home.Add") };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        string path = dlg.FileName;
        string key = Limits.Key(Path.GetFileName(path));
        string name = GameLibrary.Find(path)?.Name ?? FriendlyName(path);
        var seen = _s.GameHistory.FirstOrDefault(h => string.Equals(h.Exe, key, StringComparison.OrdinalIgnoreCase));
        if (seen == null) _s.GameHistory.Add(seen = new GameSeen { Exe = key, Name = name });
        _s.NotifyProfiles();   // salva le impostazioni
        Rebuild();
        BuildHome();
        if (_items.FirstOrDefault(i => i.Exes.Contains(key)) is { } it) { it.ExePath ??= path; Open(it); }
    }

    private static string FriendlyName(string path)
    {
        try
        {
            var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            foreach (var n in new[] { v.ProductName, v.FileDescription })
                if (!string.IsNullOrWhiteSpace(n) && n.Length < 60) return n.Trim();
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    // ------------------------------------------------------------ pagina del gioco

    public void Open(GameItem it)
    {
        _detailItem = it;
        Detail.SetContext(it);
        BuildDetailHead();
        _homeScroll.Visibility = Visibility.Collapsed;
        _detailScroll.Visibility = Visibility.Visible;
        _detailScroll.ScrollToTop();
        ViewChanged?.Invoke();
    }

    /// <summary>Dalla barra di stato: la pagina del gioco aperto, oppure l'elenco se non c'è un gioco aperto.</summary>
    public void OpenRunning()
    {
        Rebuild();
        if (_items.FirstOrDefault(i => i.Running) is { } it) Open(it); else ShowHome();
    }

    /// <summary>Solo per le schermate di prova: apre la pagina di un gioco per eseguibile (lo aggiunge se non c'è).</summary>
    public void OpenExe(string exe, string name)
    {
        Rebuild();
        var it = _items.FirstOrDefault(i => i.Exes.Contains(exe));
        if (it == null) { it = new GameItem { Key = Limits.Key(exe), Name = name }; it.Exes.Add(it.Key); }
        Open(it);
    }

    public void ShowHome()
    {
        if (_detailItem == null && _homeScroll.Visibility == Visibility.Visible) return;
        _detailItem = null;
        _detailScroll.Visibility = Visibility.Collapsed;
        _homeScroll.Visibility = Visibility.Visible;
        _sig = "";
        Refresh();
        ViewChanged?.Invoke();
    }

    private void BuildDetailHead()
    {
        _detailHead.Children.Clear();
        var it = _detailItem;
        if (it == null) return;
        var back = new Button { Padding = new Thickness(0, 2, 0, 2), Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Left };
        back.SetResourceReference(StyleProperty, "LinkButton");
        var br = new StackPanel { Orientation = Orientation.Horizontal };
        var bi = Icon(0xE72B, 12, "AccentText"); bi.Margin = new Thickness(0, 0, 8, 0);
        br.Children.Add(bi);
        br.Children.Add(T(Loc.T("Game.Back"), 13, "AccentText", true));
        back.Content = br;
        AutomationProperties.SetName(back, Loc.T("Game.Back"));   // il lettore di schermo legge il nome, non l'icona
        back.Click += (_, _) => ShowHome();
        _detailHead.Children.Add(back);

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var cover = Cover(it, 96, 0, 30);
        var coverBox = new Border { Width = 206, CornerRadius = new CornerRadius(10), Child = cover, Margin = new Thickness(0, 0, 18, 0) };
        coverBox.SizeChanged += (_, e) => coverBox.Clip = new RectangleGeometry(new Rect(0, 0, 206, e.NewSize.Height), 10, 10);
        DockPanel.SetDock(coverBox, Dock.Left);
        head.Children.Add(coverBox);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = T(it.Name, 26, "Fg", true); name.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        info.Children.Add(name);
        var g = _game();
        bool run = it.Matches(g);
        var tags = Tags(it, run ? g : null);
        tags.Margin = new Thickness(0, 8, 0, 0);
        tags.Children.Insert(it.Store != null ? 1 : 0, run ? Pill("●  " + Loc.T("Game.Running").ToUpperInvariant(), "Good", "GoodSoft") : Pill(Loc.T("Game.NotRunning").ToUpperInvariant()));
        info.Children.Add(tags);
        int n = it.Measures().Count;
        var last = T(Loc.F("Game.LastSeen", When(it.LastSeen)) + "  ·  " + MeasureCount(n), 12, "FgDim");
        last.Margin = new Thickness(0, 8, 0, 0);
        info.Children.Add(last);
        head.Children.Add(info);
        _detailHead.Children.Add(head);
    }
}
