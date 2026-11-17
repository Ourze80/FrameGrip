using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>Il limite in uso per un gioco: quello del suo profilo, altrimenti quello generale.</summary>
public static class Limits
{
    public static string Key(string exe) => exe.ToLowerInvariant();

    public static (int Fps, bool Profile) Effective(Settings s, GameInfo? g) =>
        Find(s, g) is { } f ? (f.Profile.Fps, true) : (s.LimiterFps, false);

    /// <summary>
    /// Il profilo di un gioco: per eseguibile, oppure (giochi della libreria di un negozio) per cartella, così vale
    /// anche se il gioco parte da un eseguibile diverso da quello scelto quando si è creato il profilo.
    /// </summary>
    public static (string Key, GameProfile Profile)? Find(Settings s, GameInfo? g)
    {
        if (g == null) return null;
        if (s.GameProfiles.TryGetValue(Key(g.Exe), out var p)) return (Key(g.Exe), p);
        if (g.Path != null)
            foreach (var kv in s.GameProfiles)
                if (kv.Value.Folder is { Length: > 3 } f && g.Path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return (kv.Key, kv.Value);
        return null;
    }

    /// <summary>Un profilo nuovo con i dati del negozio, se il gioco è nella libreria.</summary>
    public static GameProfile NewProfile(int fps, string name, string? path)
    {
        var lib = GameLibrary.Find(path);
        return new GameProfile
        {
            Fps = fps, DisplayName = lib?.Name ?? name, LastSeen = DateTime.Now,
            Store = lib?.Store, AppId = lib?.AppId, Folder = lib?.Folder,
        };
    }

    public static string Initials(string name)
    {
        var w = name.Split(new[] { ' ', '-', '_', ':', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (w.Length >= 2) return (char.ToUpperInvariant(w[0][0]).ToString() + char.ToUpperInvariant(w[1][0]));
        return name.Length >= 2 ? char.ToUpperInvariant(name[0]) + name.Substring(1, 1).ToUpperInvariant() : name.ToUpperInvariant();
    }
}

/// <summary>
/// La pagina di un gioco (anche non aperto), su due colonne. A sinistra: limite FPS del gioco, "Trova il limite
/// ideale" (preparazione e misura) e registrazione. A destra: risultato della misura e misure salvate da confrontare.
/// Costruita dal codice perché cambia molto da uno stato all'altro; i colori sono quelli del tema (DynamicResource).
/// Durante la misura si aggiornano solo progresso, grafico e tre numeri: niente si ricostruisce dieci volte al secondo.
/// </summary>
public sealed class FpsPage : Grid
{
    private readonly Settings _s;
    private readonly IdealMeasure _ideal;
    private readonly Func<GameInfo?> _game;

    private readonly StackPanel _limiterBox = new(), _idealBox = new(), _resultBox = new(), _recordBox = new(), _measuresBox = new(), _compareBox = new();
    public TextBlock StatusText { get; } = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };

    // limitatore
    private Slider? _slider;
    private TextBlock? _valueText;
    private CheckBox? _onlyFor;
    private readonly List<(int Fps, Button B)> _presets = new();
    private string _limiterSig = "";
    private bool _syncing;

    // misura
    private IdealState _shownState = (IdealState)(-1);
    private Border? _progFill;
    private FrameworkElement? _progTrack;
    private TextBlock? _timeText, _tAvg, _tLow, _tStut, _runTitle;
    private Ellipse? _runDot;
    private FrameGraph? _liveGraph;
    private FrameworkElement? _tipBox, _pauseBox;
    private bool _savedMeasure;


    public FpsPage(Settings s, IdealMeasure ideal, Func<GameInfo?> game)
    {
        _s = s; _ideal = ideal; _game = game;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 340 });
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        Children.Add(left);
        Children.Add(right);
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "FgDim");
        _note.SetResourceReference(TextBlock.ForegroundProperty, "Good");
        left.Children.Add(_limiterBox);
        left.Children.Add(_idealBox);
        left.Children.Add(_recordBox);
        right.Children.Add(_resultBox);
        right.Children.Add(_measuresBox);
        right.Children.Add(_compareBox);
        _ideal.Changed += OnIdealChanged;
        Loc.LanguageChanged += RebuildAll;
        _s.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Settings.LimiterFps) or nameof(Settings.GameProfiles) or nameof(Settings.LimiterEnabled)) UpdateLimiterValues();
        };
        RebuildAll();
    }

    /// <summary>Il gioco di cui si mostra la pagina (anche se non è aperto).</summary>
    public GameItem? Context => _ctx;
    private GameItem? _ctx;

    /// <summary>Chiede alla pagina Giochi di aprire un altro gioco (per esempio quello che si sta misurando).</summary>
    public event Action<GameInfo>? OpenGameRequested;
    /// <summary>"Cambia" accanto alla riga dello schermo: porta alla sezione Schermo delle Opzioni.</summary>
    public event Action? DisplaySettingsRequested;

    /// <summary>Il riquadro della registrazione (pulsante, testo e cartella vengono dalla finestra principale).</summary>
    public ContentControl RecordHost { get; } = new() { Focusable = false };

    public void SetContext(GameItem item)
    {
        bool other = _ctx == null || !string.Equals(_ctx.Key, item.Key, StringComparison.OrdinalIgnoreCase);
        _ctx = item;
        // tutto quello che riguarda la pagina di un gioco riparte da capo: niente si porta da un gioco all'altro
        if (other) { _cmpSel.Clear(); _compareOpen = false; _renameId = null; _checksOpen = null; _detailsOpen = false; _note.Visibility = Visibility.Collapsed; }
        RebuildAll();
    }

    private bool CtxRunning => _ctx != null && _ctx.Matches(_game());

    public void RebuildAll()
    {
        _limiterSig = "";
        Refresh();
        _shownState = (IdealState)(-1);
        OnIdealChanged();
        BuildRecord();
        BuildCompare();
    }

    /// <summary>Una volta al secondo dal ciclo principale: il gioco può essere partito o chiuso.</summary>
    public void Refresh()
    {
        if (_ctx == null) return;
        var g = _game();
        bool run = CtxRunning;
        string sig = $"{_ctx.Key}|{run}|{(run ? g?.RefreshHz : 0)}|{(run ? g?.Mode : null)}|{(run ? g?.FgDetected : 0)}|{_ctx.Profile(_s) != null}|{Loc.IsItalian}";
        if (sig != _limiterSig)
        {
            _limiterSig = sig;
            BuildLimiter();
            if (_ideal.State is IdealState.Idle or IdealState.Setup or IdealState.Result) { _shownState = (IdealState)(-1); OnIdealChanged(); }
        }
    }

    /// <summary>Riquadro con icona, titolo e (a destra) qualcosa di piccolo: la forma di tutte le sezioni della pagina.</summary>
    private Border SectionCard(int glyph, string title, string? sub, UIElement? right, UIElement body)
    {
        var p = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        var ic = Icon(glyph, 15, "AccentText"); ic.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(ic);
        head.Children.Add(T(title, 15, "Fg", true));
        p.Children.Add(Row(head, right));
        if (sub != null)
        {
            var st = T(sub, 12, "FgDim", wrap: true);
            st.Margin = new Thickness(0, 6, 0, 0);
            p.Children.Add(st);
        }
        if (body is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(body);
        var card = Card(p, "Panel", 14, 18);
        card.Padding = new Thickness(18, 16, 18, 18);
        card.Margin = new Thickness(0, 0, 0, 16);
        return card;
    }

    /// <summary>Il simbolo ⓘ con la spiegazione: per i nomi tecnici (1% low, frame generation...).</summary>
    private static TextBlock InfoTip(string title, string text)
    {
        var i = Icon(0xE946, 11, "FgDim");
        i.Margin = new Thickness(5, 0, 0, 0);
        i.Cursor = Cursors.Help;
        var tip = new StackPanel { MaxWidth = 300 };
        tip.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        tip.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        i.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(i, 150);
        return i;
    }

    private static StackPanel Label(string text, double size, string brush, UIElement? tip)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        p.Children.Add(T(text, size, brush));
        if (tip != null) p.Children.Add(tip);
        return p;
    }

    // ------------------------------------------------------------ mattoncini

    private static TextBlock T(string text, double size = 12, string brush = "Fg", bool bold = false, bool wrap = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        if (wrap) t.TextWrapping = TextWrapping.Wrap;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static TextBlock Icon(int glyph, double size, string brush)
    {
        var t = new TextBlock
        {
            Text = char.ConvertFromUtf32(glyph), FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static TextBlock Section(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(StyleProperty, "Section");
        return t;
    }

    private static Border Card(UIElement child, string bg = "Control", double radius = 12, double pad = 16, string border = "Line")
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), Padding = new Thickness(pad), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        b.SetResourceReference(Border.BorderBrushProperty, border);
        return b;
    }

    private static Button Btn(string text, bool accent, RoutedEventHandler click, double height = 36, double size = 13)
    {
        var b = new Button { Content = text, Height = height, FontSize = size, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.SetResourceReference(StyleProperty, accent ? "AccentButton" : "DarkButton");
        b.Click += click;
        return b;
    }

    private static Border Pill(string text, string fg, string bg, double size = 10)
    {
        var b = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 2), VerticalAlignment = VerticalAlignment.Center, Child = T(text, size, fg, true) };
        b.SetResourceReference(Border.BackgroundProperty, bg);
        return b;
    }

    private static UIElement Check(bool ok, string text, string? warnBrush = null)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        var ic = ok ? Icon(0xE73E, 11, "Good") : Icon(0xE7BA, 12, warnBrush ?? "Warn");
        ic.Width = 18;
        p.Children.Add(ic);
        var t = T(text, 12, ok ? "Fg" : warnBrush ?? "Warn", wrap: true);
        t.MaxWidth = 300;
        p.Children.Add(t);
        return p;
    }

    /// <summary>Scelte a pastiglia (Durata, Frame generation): una sola attiva.</summary>
    private static StackPanel Chips(string[] labels, int selected, Action<int> pick, double width = 56)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < labels.Length; i++)
        {
            int k = i;
            var b = new Button { Content = labels[i], Width = width, Height = 30, FontSize = 12, Margin = new Thickness(i > 0 ? 6 : 0, 0, 0, 0), Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "DarkButton");
            if (i == selected)
            {
                b.SetResourceReference(Control.BackgroundProperty, "AccentSoft");
                b.SetResourceReference(Control.BorderBrushProperty, "Accent");
                b.FontWeight = FontWeights.SemiBold;
                // chi usa un lettore di schermo deve sapere quale scelta è attiva
                AutomationProperties.SetItemStatus(b, Loc.T("Common.Selected"));
            }
            b.Click += (_, _) => pick(k);
            p.Children.Add(b);
        }
        return p;
    }

    private static DockPanel Row(UIElement left, UIElement? right, double top = 0)
    {
        var d = new DockPanel { Margin = new Thickness(0, top, 0, 0), LastChildFill = true };
        if (right != null) { DockPanel.SetDock(right, Dock.Right); d.Children.Add(right); }
        d.Children.Add(left);
        return d;
    }

    /// <summary>Riquadro d'avviso (troppo breve, pausa, gioco chiuso...): icona tonda, titolo, testo, pulsanti.</summary>
    private static Border Notice(int glyph, string color, string soft, string title, string body, double? bar, params (string Text, bool Accent, RoutedEventHandler Click)[] buttons)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Child = Icon(glyph, 13, color) };
        ((TextBlock)circle.Child).HorizontalAlignment = HorizontalAlignment.Center;
        circle.SetResourceReference(Border.BackgroundProperty, soft);
        g.Children.Add(circle);
        var col = new StackPanel();
        Grid.SetColumn(col, 1);
        var title2 = T(title, 14, "Fg", true);
        title2.Margin = new Thickness(0, 3, 0, 6);
        col.Children.Add(title2);
        col.Children.Add(T(body, 12, "FgDim", wrap: true));
        g.Children.Add(col);
        var outer = new StackPanel();
        outer.Children.Add(g);
        if (bar is { } f)
        {
            var track = new Grid { Height = 6, Margin = new Thickness(0, 12, 0, 0) };
            var tb = new Border { CornerRadius = new CornerRadius(3) };
            tb.SetResourceReference(Border.BackgroundProperty, "LineStrong");
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
            fill.SetResourceReference(Border.BackgroundProperty, "FgDim");
            track.Children.Add(tb); track.Children.Add(fill);
            track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * Math.Clamp(f, 0, 1);
            outer.Children.Add(track);
        }
        if (buttons.Length > 0)
        {
            var bp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
            foreach (var (text, accent, click) in buttons)
            {
                var b = Btn(text, accent, click);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(0, 0, 8, 0);
                b.Padding = new Thickness(16, 0, 16, 0);
                bp.Children.Add(b);
            }
            outer.Children.Add(bp);
        }
        return Card(outer);
    }

    private static string N(double v, string fmt = "0") => v.ToString(fmt, Loc.IsItalian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.InvariantCulture);

    private void ShowNote(string text, bool warn = false)
    {
        _note.Text = text;
        _note.SetResourceReference(TextBlock.ForegroundProperty, warn ? "Warn" : "Good");
        _note.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------ limitatore

    private CheckBox? _useAll;

    private void BuildLimiter()
    {
        _limiterBox.Children.Clear();
        _presets.Clear();
        if (_ctx == null) return;
        var body = new StackPanel();

        // interruttore e valore
        var toggle = new CheckBox { Content = Loc.T("Game.LimitOn"), FontWeight = FontWeights.SemiBold };
        toggle.SetResourceReference(StyleProperty, "Toggle");
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Settings.LimiterEnabled)) { Source = _s, Mode = BindingMode.TwoWay });
        _valueText = T("", 28, "Fg", true);
        _valueText.MinWidth = 52; _valueText.TextAlignment = TextAlignment.Right; _valueText.Margin = new Thickness(18, 0, 0, 0);
        var val = new StackPanel { Orientation = Orientation.Horizontal };
        val.Children.Add(_valueText);
        var unit = T("FPS", 11, "FgDim"); unit.VerticalAlignment = VerticalAlignment.Bottom; unit.Margin = new Thickness(6, 0, 0, 6);
        val.Children.Add(unit);
        body.Children.Add(Row(toggle, val));

        _slider = new Slider { Minimum = 20, Maximum = 360, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 8, 0, 10) };
        _slider.ValueChanged += (_, e) => { if (!_syncing) SetLimit((int)e.NewValue); };
        AutomationProperties.SetName(_slider, Loc.T("Game.LimitTitle"));
        body.Children.Add(_slider);

        var presets = new UniformGrid { Rows = 1, Margin = new Thickness(0, 0, -6, 4) };
        foreach (int v in new[] { 30, 60, 90, 120, 144, 165, 240 })
        {
            var b = new Button { Content = v.ToString(CultureInfo.InvariantCulture), Height = 32, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(0) };
            b.SetResourceReference(StyleProperty, "DarkButton");
            int fps = v;
            b.Click += (_, _) => SetLimit(fps);
            _presets.Add((v, b));
            presets.Children.Add(b);
        }
        body.Children.Add(presets);

        // per questo gioco o come tutti gli altri: due pillole che si escludono
        var scope = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        _onlyFor = new CheckBox { Content = Loc.T("Game.OnlyThis"), Margin = new Thickness(0, 0, 8, 6) };
        _onlyFor.SetResourceReference(StyleProperty, "Chip");
        _onlyFor.Click += (_, _) => SetOnlyFor(true);
        _useAll = new CheckBox { Margin = new Thickness(0, 0, 0, 6) };
        _useAll.SetResourceReference(StyleProperty, "Chip");
        _useAll.Click += (_, _) => SetOnlyFor(false);
        scope.Children.Add(_onlyFor);
        scope.Children.Add(_useAll);
        body.Children.Add(scope);

        if (StatusText.Parent is Panel old) old.Children.Remove(StatusText);
        body.Children.Add(StatusText);
        _limiterBox.Children.Add(SectionCard(0xEC4A, Loc.T("Game.LimitTitle"), Loc.T("Game.LimitSub"), null, body));
        UpdateLimiterValues();
    }

    private void UpdateLimiterValues()
    {
        if (_slider == null || _valueText == null || _ctx == null) return;
        var prof = _ctx.Profile(_s);
        int fps = prof?.Profile.Fps ?? _s.LimiterFps;
        _syncing = true;
        try
        {
            _valueText.Text = fps.ToString(CultureInfo.InvariantCulture);
            _slider.Value = fps;
            foreach (var (v, b) in _presets)
            {
                if (v == fps) { b.SetResourceReference(Control.BackgroundProperty, "AccentSoft"); b.SetResourceReference(Control.BorderBrushProperty, "Accent"); }
                else { b.SetResourceReference(Control.BackgroundProperty, "Control"); b.SetResourceReference(Control.BorderBrushProperty, "Line"); }
            }
            if (_onlyFor != null) _onlyFor.IsChecked = prof != null;
            if (_useAll != null)
            {
                _useAll.IsChecked = prof == null;
                _useAll.Content = Loc.F("Game.UseAll", _s.LimiterFps);
            }
        }
        finally { _syncing = false; }
        if (_limiterSig.Length > 0 && !_limiterSig.EndsWith($"|{prof != null}|{Loc.IsItalian}", StringComparison.Ordinal)) { _limiterSig = ""; Dispatcher.BeginInvoke(Refresh); }
    }

    /// <summary>Cursore e scelte rapide: cambiano il profilo del gioco se ce l'ha, altrimenti il limite di tutti gli altri.</summary>
    private void SetLimit(int fps)
    {
        if (_ctx?.Profile(_s) is { } found)
        {
            var p = found.Profile;
            if (p.Fps == fps) return;
            p.Fps = Math.Clamp(fps, 20, 500);
            _s.NotifyProfiles();
        }
        else _s.LimiterFps = fps;
    }

    private void SetOnlyFor(bool on)
    {
        if (_ctx == null) return;
        var prof = _ctx.Profile(_s);
        if (on && prof == null) EnsureProfile(_s.LimiterFps);
        else if (!on && prof != null) _s.GameProfiles.Remove(prof.Value.Key);
        _s.NotifyProfiles();
        UpdateLimiterValues();
    }

    /// <summary>
    /// Il profilo del gioco della pagina, creato se manca. Con il gioco aperto la chiave è l'eseguibile che gira
    /// davvero; la cartella del negozio fa valere il profilo anche se il gioco parte da un altro eseguibile.
    /// </summary>
    private GameProfile EnsureProfile(int fps)
    {
        var have = _ctx!.Profile(_s);
        if (have != null) return have.Value.Profile;
        var g = _game();
        bool run = _ctx.Matches(g);
        string key = run ? Limits.Key(g!.Exe) : _ctx.Key;
        var p = Limits.NewProfile(fps, _ctx.Name, run ? g!.Path : _ctx.ExePath);
        p.DisplayName = _ctx.Name;
        p.Folder ??= _ctx.Folder;
        p.Store ??= _ctx.Store;
        p.AppId ??= _ctx.AppId;
        _s.GameProfiles[key] = p;
        _ctx.Exes.Add(key);
        return p;
    }

    /// <summary>Mette un limite nel profilo del gioco della pagina (lo crea se manca).</summary>
    private void SaveProfile(int fps, bool enable)
    {
        if (_ctx == null) return;
        EnsureProfile(fps).Fps = fps;
        if (enable) _s.LimiterEnabled = true;
        _s.NotifyProfiles();
    }

    // ------------------------------------------------------------ registrazione

    private void BuildRecord()
    {
        _recordBox.Children.Clear();
        if (RecordHost.Parent is Panel old) old.Children.Remove(RecordHost);
        var body = new StackPanel();
        body.Children.Add(RecordHost);
        _recordBox.Children.Add(SectionCard(0xE7C8, Loc.T("Game.RecordTitle"), Loc.T("Game.RecordSub"), null, body));
    }

    // ------------------------------------------------------------ limite ideale

    private void OnIdealChanged()
    {
        if (_ideal.State != _shownState)
        {
            _shownState = _ideal.State;
            BuildIdeal();
        }
        else UpdateLive();
    }

    private bool? _checksOpen;   // null: aperti da soli se il gioco non è in corso (ci sono poche righe)
    private bool _detailsOpen;

    /// <summary>
    /// A sinistra la preparazione (o la misura in corso), a destra il risultato. La misura riguarda il gioco
    /// aperto: nella pagina di un altro gioco si può solo preparare, e si dice dove sta andando la misura.
    /// </summary>
    private void BuildIdeal()
    {
        _idealBox.Children.Clear();
        _resultBox.Children.Clear();
        _progFill = null; _liveGraph = null; _timeText = null;
        if (_ctx == null) return;
        var measured = _ideal.Game;
        bool active = _ideal.State is not (IdealState.Idle or IdealState.Setup);
        bool mine = active && measured != null ? _ctx.Matches(measured) : CtxRunning;
        if (active && measured != null && !mine && _ideal.State != IdealState.Result)
        {
            _idealBox.Children.Add(Notice(0xE946, "AccentText", "AccentSoft", Loc.T("Ideal.Title"), Loc.F("Game.MeasuringOther", measured.Name), null,
                (Loc.T("Game.OpenIt"), true, (_, _) => OpenGameRequested?.Invoke(measured))));
            ((FrameworkElement)_idealBox.Children[0]).Margin = new Thickness(0, 0, 0, 16);
            _resultBox.Children.Add(NoResultCard());
            return;
        }
        UIElement left = _ideal.State switch
        {
            IdealState.LimiterNotice => Notice(0xE7BA, "Warn", "WarnSoft", Loc.T("Ideal.LimiterTitle"),
                Loc.F("Ideal.LimiterBody", Limits.Effective(_s, _game()).Fps), null,
                (Loc.T("Ideal.LimiterOk"), true, (_, _) => StartNow()),
                (Loc.T("Ideal.Cancel"), false, (_, _) => _ideal.OpenSetup())),
            IdealState.NoGame => Notice(0xE946, "AccentText", "AccentSoft", Loc.T("Ideal.NoGameTitle"), Loc.T("Ideal.NoGameBody"), null,
                (Loc.T("Ideal.Start"), false, (_, _) => TryStart()),
                (Loc.T("Ideal.Cancel"), false, (_, _) => _ideal.Close())),
            IdealState.Countdown or IdealState.Measuring => SectionCard(0xEA80, Loc.T("Ideal.Title"), null, null, MeasureCard()),
            IdealState.Analyzing => SectionCard(0xEA80, Loc.T("Ideal.Title"), Loc.T("Ideal.Analyzing"), null, new StackPanel()),
            IdealState.TooShort => Notice(0xE7BA, "Warn", "WarnSoft", Loc.T("Ideal.ShortTitle"),
                Loc.F("Ideal.ShortBody", (int)(_ideal.Result?.CleanSec ?? _ideal.CleanSec), (int)_ideal.MinCleanSec), null,
                (Loc.T("Ideal.ShortContinue"), true, (_, _) => _ideal.Resume()),
                (Loc.T("Ideal.Cancel"), false, (_, _) => _ideal.Cancel())),
            IdealState.GameClosed => Notice(0xE783, "Danger", "DangerSoft", Loc.T("Ideal.ClosedTitle"),
                Loc.F("Ideal.ClosedBody", (int)_ideal.ClosedSeconds), null,
                (Loc.T("Ideal.RedoRun"), true, (_, _) => _ideal.OpenSetup()),
                (Loc.T("Ideal.Close"), false, (_, _) => _ideal.Close())),
            _ => SetupCard(),
        };
        if (left is FrameworkElement lf && lf.Margin == default) lf.Margin = new Thickness(0, 0, 0, 16);
        _idealBox.Children.Add(left);
        if (_ideal.State is IdealState.Countdown or IdealState.Measuring) UpdateLive();
        _resultBox.Children.Add(_ideal.State == IdealState.Result && mine && _ideal.Result != null ? ResultCard() : NoResultCard());
    }

    private UIElement NoResultCard()
    {
        var body = T(Loc.T(CtxRunning ? "Game.NoResultRunning" : "Game.NoResult"), 12.5, "FgDim", wrap: true);
        return SectionCard(0xE9D2, Loc.T("Game.ResultTitle"), null, null, body);
    }

    private string ScreenFoot(GameInfo? g)
    {
        var parts = new List<string>();
        if (g?.RefreshHz > 0) parts.Add($"{g.RefreshHz} Hz");
        if (_s.VrrDisplay == true) parts.Add("VRR");
        parts.Add(Loc.T("Ideal.BuiltinLower"));
        return string.Join("  ·  ", parts);
    }

    private UIElement SetupCard()
    {
        bool run = CtxRunning;
        var g = run ? _game() : null;
        var p = new StackPanel();

        // durata e frame generation sono di questo gioco: si leggono e si scrivono solo con i suoi eseguibili
        int minutes = CtxMinutes;
        int mi = minutes >= 10 ? 2 : minutes >= 5 ? 1 : 0;
        p.Children.Add(Row(T(Loc.T("Ideal.Duration"), 12.5, "FgDim"),
            Chips(new[] { Loc.F("Ideal.Minutes", 2), Loc.F("Ideal.Minutes", 5), Loc.F("Ideal.Minutes", 10) }, mi,
                i => { _s.SetPerGame(_s.IdealMinutesByGame, CtxKeys(), i == 0 ? 2 : i == 1 ? 5 : 10); BuildIdeal(); }, 60)));
        // la stima della frame generation sbaglia con i giochi a scatti: si propone, non si sceglie da sola
        p.Children.Add(Row(Label(Loc.T("Ideal.FrameGen"), 12.5, "FgDim", InfoTip(Loc.T("Ideal.FrameGen"), Loc.T("Game.FgTip"))),
            Chips(new[] { Loc.T("Ideal.No"), "x2", "x3", "x4" }, CtxFg - 1, i => { _s.SetPerGame(_s.IdealFg, CtxKeys(), i + 1); BuildIdeal(); }, 44), 10));

        // controlli: riassunti in una riga se va tutto bene, aperti se c'è qualcosa da guardare o da rispondere
        var checks = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var short_ = new List<string>();
        int problems = 0;
        void Add(bool ok, string text, string? warn = null) { checks.Children.Add(Check(ok, text, warn)); if (!ok) problems++; }
        bool ask = false;
        // gioco in corso: lo schermo su cui sta; altrimenti lo schermo principale
        int hz = g?.RefreshHz ?? 0;
        if (hz <= 0 && !run) hz = DisplayInfo.Primary().Hz;
        if (!run) Add(false, Loc.T("Game.NeedRunning"), "FgDim");
        else if (hz <= 0) Add(false, Loc.T("Ideal.CheckHzUnknown"));
        // la riga dello schermo c'è sempre, anche a gioco spento: G-Sync si sceglie qui o nelle Opzioni
        var vrrWord = Loc.T(_s.VrrDisplay switch { true => "Display.RowYes", false => "Display.RowNo", _ => "Display.RowUnset" });
        var line = new DockPanel();
        var change = new Button { Content = Loc.T("Display.Change"), FontSize = 11.5, Padding = new Thickness(10, 3, 10, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), ToolTip = Loc.T("Display.ChangeTip") };
        change.SetResourceReference(StyleProperty, "DarkButton");
        AutomationProperties.SetHelpText(change, Loc.T("Display.ChangeTip"));
        change.Click += (_, _) => DisplaySettingsRequested?.Invoke();
        DockPanel.SetDock(change, Dock.Right);
        line.Children.Add(change);
        line.Children.Add(Check(_s.VrrDisplay != null, Loc.F("Display.Row", hz > 0 ? $"{hz} Hz" : "", vrrWord).Replace("  ", " "), _s.VrrDisplay == null ? "Warn" : null));
        checks.Children.Add(line);
        if (_s.VrrDisplay == null)
        {
            // si può rispondere anche qui, senza cambiare pagina
            problems++;
            ask = run;
            var askRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(18, 2, 0, 2) };
            askRow.Children.Add(Chips(new[] { Loc.T("Ideal.Yes"), Loc.T("Ideal.No") }, -1, i => { _s.VrrDisplay = i == 0; BuildIdeal(); }, 48));
            checks.Children.Add(askRow);
            var note = T(Loc.T("Ideal.VrrNote"), 11, "FgDim", wrap: true);
            note.Margin = new Thickness(18, 2, 0, 4);
            checks.Children.Add(note);
        }
        else if (run && hz > 0)
        {
            checks.Children.Add(Check(true, _s.VrrDisplay == true ? Loc.F("Ideal.CheckVrr", hz, hz - 3) : Loc.F("Ideal.CheckNoVrr", hz)));
            short_.Add(_s.VrrDisplay == true ? Loc.F("Game.ShortVrr", hz - 3) : $"{hz} Hz");
        }
        Add(true, Loc.T("Ideal.CheckLimiter"));
        var mode = g?.Mode ?? WindowMode.Unknown;
        if (run)
        {
            switch (mode)
            {
                case WindowMode.Fullscreen: Add(true, Loc.T("Ideal.CheckFull")); short_.Insert(0, Loc.T("Fps.ModeFull").ToLowerInvariant()); break;
                case WindowMode.Borderless: Add(false, Loc.T("Ideal.CheckBorderless")); break;
                case WindowMode.Windowed: Add(false, Loc.T("Ideal.CheckWindowed")); break;
                default: checks.Children.Add(Check(false, Loc.T("Ideal.CheckModeUnknown"), "FgDim")); break;
            }
            int fgDet = g?.FgDetected ?? 1;
            if (fgDet > 1) Add(false, Loc.F("Ideal.CheckFg", fgDet));
            else { checks.Children.Add(Check(true, Loc.T("Ideal.CheckNoFg"))); short_.Add(Loc.T("Game.ShortNoFg")); }
        }

        bool open = ask || (_checksOpen ?? !run);
        var sumText = problems == 0 ? Loc.F("Game.Ready", string.Join(", ", short_)) : Loc.F("Game.ToCheck", problems);
        var sum = new DockPanel { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var chev = Icon(open ? 0xE70E : 0xE70D, 10, "FgDim"); chev.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(chev, Dock.Right);
        sum.Children.Add(chev);
        var sumIc = Icon(problems == 0 ? 0xE73E : 0xE7BA, 12, problems == 0 ? "Good" : "Warn"); sumIc.Width = 20; sumIc.VerticalAlignment = VerticalAlignment.Center;
        sum.Children.Add(sumIc);
        sum.Children.Add(T(sumText, 12, "Fg", wrap: true));
        sum.MouseLeftButtonUp += (_, _) => { _checksOpen = !open; BuildIdeal(); };
        var sumBox = new StackPanel();
        sumBox.Children.Add(sum);
        if (open) sumBox.Children.Add(checks);
        var sumCard = Card(sumBox, problems == 0 ? "GoodSoft" : "Control", 10, 10, problems == 0 ? "GoodSoft" : "Line");
        sumCard.Margin = new Thickness(0, 14, 0, 0);
        p.Children.Add(sumCard);

        if (run && mode is WindowMode.Borderless or WindowMode.Windowed)
        {
            var call = Notice(0xE7BA, "Warn", "WarnSoft", Loc.T(mode == WindowMode.Borderless ? "Ideal.BorderlessTitle" : "Ideal.WindowedTitle"), Loc.T("Ideal.BorderlessBody"), null);
            call.Margin = new Thickness(0, 12, 0, 0);
            p.Children.Add(call);
        }

        var startRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var start = Btn("▶  " + Loc.T("Ideal.Start"), true, (_, _) => TryStart(), 40);
        start.IsEnabled = run;
        if (!run) start.Opacity = 0.45;   // il pulsante colorato spento deve vedersi spento anche col tema chiaro
        start.MinWidth = 170;
        DockPanel.SetDock(start, Dock.Left);
        startRow.Children.Add(start);
        var side = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(T(run ? Loc.T("Ideal.StartFoot") : Loc.T("Game.NeedRunning"), 11, "FgDim", wrap: true));
        var sound = new CheckBox { Content = Loc.T("Ideal.Sound"), FontSize = 11.5, Margin = new Thickness(0, 4, 0, 0) };
        sound.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Settings.IdealSound)) { Source = _s, Mode = BindingMode.TwoWay });
        side.Children.Add(sound);
        startRow.Children.Add(side);
        p.Children.Add(startRow);
        // la strada più comoda: il tasto, premuto mentre si gioca (stesse scelte di qui sopra)
        if (_s.IdealVk != 0)
        {
            var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            keyRow.Children.Add(T(Loc.T("Keys.IdealInGame"), 12, "FgDim"));
            var cap = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            cap.SetResourceReference(Border.BackgroundProperty, "Control");
            cap.SetResourceReference(Border.BorderBrushProperty, "LineStrong");
            cap.Child = T(_s.IdealName, 11, "Fg", true);
            keyRow.Children.Add(cap);
            keyRow.Children.Add(T(Loc.T("Keys.IdealInGameEnd"), 12, "FgDim"));
            p.Children.Add(keyRow);
        }
        return SectionCard(0xEA80, Loc.T("Ideal.Title"), Loc.T("Ideal.Intro"), null, p);
    }

    /// <summary>Tutti i nomi con cui si conosce il gioco della pagina (più quello che sta girando, se è lui).</summary>
    private List<string> CtxKeys()
    {
        var keys = new List<string>();
        if (_ctx != null) { keys.Add(_ctx.Key); keys.AddRange(_ctx.Exes); }
        if (CtxRunning && _game() is { } g) keys.Add(Limits.Key(g.Exe));
        return keys.Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private int CtxFg => Math.Clamp(Settings.PerGame(_s.IdealFg, CtxKeys(), 1), 1, 4);
    private int CtxMinutes => Settings.PerGame(_s.IdealMinutesByGame, CtxKeys(), _s.IdealMinutes);

    private void TryStart()
    {
        var g = _game();
        if (g == null || g.Stats == null) { _ideal.ShowNotice(IdealState.NoGame); return; }
        if (_s.LimiterEnabled && _ideal.State != IdealState.LimiterNotice) { _ideal.ShowNotice(IdealState.LimiterNotice); return; }
        StartNow();
    }

    private void StartNow()
    {
        var g = _game();
        if (g == null || g.Stats == null) { _ideal.ShowNotice(IdealState.NoGame); return; }
        _savedMeasure = false;
        _note.Visibility = Visibility.Collapsed;
        // le scelte restano salvate per questo gioco: il tasto in gioco userà le stesse
        var keys = CtxKeys();
        _s.SetPerGame(_s.IdealFg, keys, CtxFg);
        _s.SetPerGame(_s.IdealMinutesByGame, keys, CtxMinutes);
        _ideal.Start(g, CtxMinutes, _s.VrrDisplay == true, CtxFg);
    }

    private UIElement MeasureCard()
    {
        var p = new StackPanel();
        _runDot = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 8, 0) };
        _runDot.SetResourceReference(Shape.FillProperty, "Danger");
        _runTitle = T("", 14, "Fg", true);
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(_runDot); head.Children.Add(_runTitle);
        _timeText = T("", 12, "FgDim");
        _timeText.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        p.Children.Add(Row(head, _timeText));

        var track = new Grid { Height = 6, Margin = new Thickness(0, 12, 0, 12) };
        var tb = new Border { CornerRadius = new CornerRadius(3) };
        tb.SetResourceReference(Border.BackgroundProperty, "LineStrong");
        _progFill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        _progFill.SetResourceReference(Border.BackgroundProperty, "Accent");
        track.Children.Add(tb); track.Children.Add(_progFill);
        _progTrack = track;
        p.Children.Add(track);

        _liveGraph = new FrameGraph { Height = 150, Label = Loc.T("Ideal.GraphLabel"), DurationSec = _ideal.DurationSec };
        _liveGraph.Lines.Add(new FrameGraph.Series(_ideal.LiveGraph, (Brush)FindResource("Accent")));
        p.Children.Add(_liveGraph);

        var tiles = new UniformGrid { Rows = 1, Margin = new Thickness(0, 12, 0, 0) };
        TextBlock Tile(string label)
        {
            var c = new StackPanel();
            c.Children.Add(T(label, 11, "FgDim"));
            var v = T("—", 20, "Fg", true);
            v.Margin = new Thickness(0, 2, 0, 0);
            c.Children.Add(v);
            var b = Card(c, "Panel", 10, 10);
            b.Padding = new Thickness(12, 8, 12, 8);
            b.Margin = new Thickness(tiles.Children.Count > 0 ? 8 : 0, 0, 0, 0);
            tiles.Children.Add(b);
            return v;
        }
        _tAvg = Tile(Loc.T("Ideal.TileAvg"));
        _tLow = Tile(Loc.T("Ideal.TileLow"));
        _tStut = Tile(Loc.T("Ideal.TileStutter"));
        p.Children.Add(tiles);

        var tipGrid = new Grid();
        tipGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        tipGrid.ColumnDefinitions.Add(new ColumnDefinition());
        tipGrid.Children.Add(Icon(0xE946, 15, "AccentText"));
        var tipText = T(Loc.T("Ideal.Tip"), 12, "Fg", wrap: true);
        Grid.SetColumn(tipText, 1);
        tipGrid.Children.Add(tipText);
        var tip = Card(tipGrid, "AccentSoft", 10, 12, "AccentSoft");
        tip.Margin = new Thickness(0, 14, 0, 0);
        _tipBox = tip;
        p.Children.Add(tip);
        var pause = Notice(0xE769, "FgDim", "ControlHover", Loc.T("Ideal.Paused"), Loc.T("Ideal.PausedBody"), null);
        pause.Margin = new Thickness(0, 14, 0, 0);
        pause.Visibility = Visibility.Collapsed;
        _pauseBox = pause;
        p.Children.Add(pause);

        var sus = Check(true, Loc.T("Ideal.Suspended"));
        ((FrameworkElement)sus).Margin = new Thickness(0, 10, 0, 6);
        p.Children.Add(sus);
        p.Children.Add(Btn(Loc.T("Ideal.CancelRun"), false, (_, _) => _ideal.Cancel()));
        return Card(p);
    }

    private void UpdateLive()
    {
        if (_runTitle == null || _timeText == null || _progFill == null || _progTrack == null) return;
        if (_ideal.State == IdealState.Countdown)
        {
            _runTitle.Text = Loc.F("Ideal.Countdown", _ideal.CountdownLeft);
            _runDot!.SetResourceReference(Shape.FillProperty, "Accent");
            _timeText.Text = $"0:00 / {IdealMeasure.Clock(_ideal.DurationSec)}";
            _progFill.Width = 0;
            return;
        }
        if (_ideal.State != IdealState.Measuring) return;
        bool paused = _ideal.Paused;
        _runTitle.Text = Loc.T(paused ? "Ideal.Paused" : "Ideal.Running");
        _runDot!.SetResourceReference(Shape.FillProperty, paused ? "FgDim" : "Danger");
        _timeText.Text = $"{IdealMeasure.Clock(_ideal.CleanSec)} / {IdealMeasure.Clock(_ideal.DurationSec)}";
        _progFill.Width = _progTrack.ActualWidth * _ideal.Progress;
        if (_tipBox != null) _tipBox.Visibility = paused ? Visibility.Collapsed : Visibility.Visible;
        if (_pauseBox != null) _pauseBox.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        double avg = _ideal.LiveAvgFps, low = _ideal.LiveLow1Fps;
        _tAvg!.Text = avg > 0 ? N(avg) : "—";
        _tLow!.Text = low > 0 ? N(low) : "—";
        _tStut!.Text = _ideal.LiveStutters.ToString(CultureInfo.InvariantCulture);
        if (_liveGraph != null)
        {
            _liveGraph.Lines[0] = _liveGraph.Lines[0] with { Upto = _ideal.Progress };
            _liveGraph.Corner = avg > 0 ? $"{N(avg)} FPS" : null;
            _liveGraph.InvalidateVisual();
        }
    }

    private UIElement ResultCard()
    {
        var r = _ideal.Result!;
        var g = _ideal.Game;
        var opt = _ideal.SelectedOption!;
        var p = new StackPanel();

        var tiles = new UniformGrid { Rows = 1 };
        var opts = new[] { (Loc.T("Ideal.OptRec"), r.Recommended), (Loc.T("Ideal.OptBal"), r.Balanced), (Loc.T("Ideal.OptMax"), r.Max) };
        for (int i = 0; i < 3; i++)
        {
            var (label, o) = opts[i];
            bool sel = i == _ideal.Selected;
            var c = new StackPanel();
            var l = T(label, 9.5, sel ? "AccentText" : "FgDim", true); l.HorizontalAlignment = HorizontalAlignment.Center;
            var v = T(o.Cap.ToString(CultureInfo.InvariantCulture), 26, "Fg", true); v.HorizontalAlignment = HorizontalAlignment.Center;
            double d = Math.Round(o.DipsPerMin);
            // due proposte uguali: si dice, invece di ripetere lo stesso numero come se fosse un'altra scelta
            bool same = i > 0 && o.Cap == opts[i - 1].Item2.Cap;
            var s = T(same ? Loc.F("Ideal.SameAs", opts[i - 1].Item1.ToLowerInvariant()) : d <= 1 ? Loc.T("Ideal.DipPerMin") : Loc.F("Ideal.DipsPerMin", N(d)), 10, sel ? "Fg" : "FgDim");
            s.HorizontalAlignment = HorizontalAlignment.Center;
            c.Children.Add(l); c.Children.Add(v); c.Children.Add(s);
            var b = Card(c, sel ? "AccentSoft" : "Control", 10, 10, sel ? "Accent" : "Line");
            b.BorderThickness = new Thickness(sel ? 1.5 : 1);
            b.Margin = new Thickness(i > 0 ? 8 : 0, 0, 0, 0);
            b.Cursor = Cursors.Hand;
            int k = i;
            b.MouseLeftButtonUp += (_, _) => { _ideal.Selected = k; BuildIdeal(); };
            tiles.Children.Add(b);
        }
        p.Children.Add(tiles);

        string explain = _ideal.Selected switch
        {
            1 => Loc.F("Ideal.ExplainBal", N(Math.Round(r.Balanced.DipsPerMin))),
            2 => Loc.F("Ideal.ExplainMax", N(Math.Round(r.Max.DipsPerMin))),
            _ => Loc.F("Ideal.ExplainRec", r.Recommended.Cap),
        };
        if (r.Ceiling < 499)
            explain += r.ScreenIsLimit && _ideal.Selected == 0 ? Loc.F("Ideal.ScreenLimit", (int)Math.Floor(r.Ceiling)) : Loc.F("Ideal.ScreenCeiling", (int)Math.Floor(r.Ceiling));
        var ex = T(explain, 12, "FgDim", wrap: true);
        ex.Margin = new Thickness(0, 10, 0, r.Hitches > 0 ? 2 : 10);
        p.Children.Add(ex);
        if (r.Hitches > 0)
        {
            // scatti brevi del gioco: contati a parte, un limite non li toglie
            var hl = T(r.Hitches == 1 ? Loc.T("Ideal.HitchOne") : Loc.F("Ideal.Hitches", r.Hitches), 12, "FgDim", wrap: true);
            hl.Margin = new Thickness(0, 0, 0, 10);
            p.Children.Add(hl);
        }

        // tre numeri con nomi semplici; il nome tecnico sta nella ⓘ
        var metrics = new UniformGrid { Rows = 1 };
        void Metric(UIElement label, string value, string brush = "Fg", double size = 18)
        {
            var c = new StackPanel();
            c.Children.Add(label);
            var v = T(value, size, brush, true); v.Margin = new Thickness(0, 3, 0, 0);
            c.Children.Add(v);
            var b = Card(c, "Control", 10, 10);
            b.Padding = new Thickness(12, 8, 8, 8);
            b.Margin = new Thickness(metrics.Children.Count > 0 ? 8 : 0, 0, 0, 0);
            metrics.Children.Add(b);
        }
        Metric(Label(Loc.T("Game.AvgFps"), 10.5, "FgDim", null), N(r.AvgFps));
        Metric(Label(Loc.T("Game.Worst"), 10.5, "FgDim", InfoTip(Loc.T("Game.WorstTech"), Loc.T("Metric.low1"))), N(r.Low1Fps));
        string stab = r.Stability switch { Stability.Excellent => "Stab.Excellent", Stability.Good => "Stab.Good", Stability.Fair => "Stab.Fair", _ => "Stab.Poor" };
        Metric(Label(Loc.T("Ideal.MStab"), 10.5, "FgDim", InfoTip(Loc.T("Ideal.MStab"), Loc.T("Game.StabTip"))), Loc.T(stab), r.Stability <= Stability.Good ? "Good" : "Warn", 16);
        p.Children.Add(metrics);

        // Applica = limite in uso subito e salvato nel profilo di questo gioco
        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var apply = Btn(Loc.F("Ideal.Apply", opt.Cap), true, (_, _) =>
        {
            if (g == null) return;
            ApplyFor(g, opt.Cap);
            ShowNote(Loc.F("Game.Applied", opt.Cap, g.Name));
        }, 38);
        apply.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(apply);
        var saveM = Btn(_savedMeasure ? Loc.T("Ideal.Saved") : Loc.T("Ideal.SaveMeasure"), false, (_, _) =>
        {
            if (g == null || _savedMeasure) return;
            int n = (_ctx?.Measures().Count ?? MeasureStore.ForGame(g.Exe).Count) + 1;
            MeasureStore.Add(MeasureStore.From(r, g.Exe, g.Name, Loc.F("Cmp.DefaultName", n)));
            _savedMeasure = true;
            BuildIdeal();
            BuildCompare();
        }, 38, 13);
        saveM.IsEnabled = !_savedMeasure;
        saveM.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(saveM);
        var redo = Btn(Loc.T("Ideal.Redo"), false, (_, _) => { _note.Visibility = Visibility.Collapsed; _ideal.OpenSetup(); }, 38, 13);
        redo.Margin = new Thickness(0, 0, 8, 8);
        buttons.Children.Add(redo);
        p.Children.Add(buttons);
        if (_note.Parent is Panel oldNote) oldNote.Children.Remove(_note);
        p.Children.Add(_note);

        // grafico, avvisi e cause: dietro un clic
        var more = new Button { Padding = new Thickness(0), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand };
        more.SetResourceReference(StyleProperty, "LinkButton");
        var moreRow = new StackPanel { Orientation = Orientation.Horizontal };
        moreRow.Children.Add(T(Loc.T("Game.Details"), 12.5, "AccentText", true));
        var chev = Icon(_detailsOpen ? 0xE70E : 0xE70D, 10, "AccentText"); chev.Margin = new Thickness(8, 2, 0, 0);
        moreRow.Children.Add(chev);
        more.Content = moreRow;
        more.Click += (_, _) => { _detailsOpen = !_detailsOpen; BuildIdeal(); };
        p.Children.Add(more);

        if (_detailsOpen)
        {
            var d = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var graph = new FrameGraph { Height = 140, Label = Loc.T("Ideal.GraphLabel"), DurationSec = r.CleanSec, CapMs = 1000.0 * r.FgMultiplier / opt.Cap, Marks = r.Marks };
            graph.Lines.Add(new FrameGraph.Series(r.Graph, (Brush)FindResource("Accent")));
            d.Children.Add(graph);
            var capLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 10) };
            var dash = new Line { X1 = 0, X2 = 14, Y1 = 0, Y2 = 0, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 2.5, 2 }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            dash.SetResourceReference(Shape.StrokeProperty, "Warn");
            capLine.Children.Add(dash);
            capLine.Children.Add(T(Loc.F("Ideal.CapLine", opt.Cap, N(1000.0 / opt.Cap, "0.0"), N(Math.Floor(opt.PctBelow * 10) / 10, "0.#")), 11, "FgDim"));
            d.Children.Add(capLine);

            var facts = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            void Fact(int glyph, string text, string brush = "AccentText", string textBrush = "Fg")
            {
                var f = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
                var ic = Icon(glyph, 12, brush); ic.Width = 20;
                f.Children.Add(ic);
                var t = T(text, 12, textBrush, wrap: true); t.MaxWidth = 380;
                f.Children.Add(t);
                facts.Children.Add(f);
            }
            Fact(0xE9D2, Loc.F("Game.WorstFact", N(r.Low01Fps)), "FgDim", "FgDim");
            if (r.Bound == "gpu") Fact(0xE9D9, Loc.F("Ideal.BoundGpu", N(r.BoundLoad)));
            else if (r.Bound == "cpu") Fact(0xE9D9, Loc.F("Ideal.BoundCpu", N(r.BoundLoad)));
            if (r.FgMultiplier > 1) Fact(0xE946, Loc.F("Ideal.FgFact", r.FgMultiplier, N(r.BaseAvgFps), N(r.AvgFps)));
            if (_ideal.Borderless) Fact(0xE7BA, Loc.T("Ideal.WindowedFact"), "Warn", "Warn");
            if (r.Stability == Stability.Poor) Fact(0xE7BA, Loc.T("Ideal.StabNote"), "Warn", "Warn");
            if (r.Below30) Fact(0xE7BA, Loc.T("Ideal.Below30"), "Warn", "Warn");
            d.Children.Add(facts);

            // cause dei cali (prova)
            var cz = new StackPanel();
            cz.Children.Add(Row(T(Loc.T("Ideal.Causes"), 12, "Fg", true), Pill(Loc.T("Ideal.Beta"), "FgDim", "ControlHover", 9)));
            string span = IdealMeasure.Clock(r.CleanSec);
            int events = r.Dips + r.Hitches;   // le cause valgono per i cali e per gli scatti brevi
            var count = T(events == 0 ? Loc.T("Ideal.CausesNone") : events == 1 ? Loc.F("Ideal.CausesOne", span) : Loc.F("Ideal.CausesCount", events, span), 11, "FgDim");
            count.Margin = new Thickness(0, 6, 0, 4);
            cz.Children.Add(count);
            void Cause(int n, string key)
            {
                if (n == 0) return;
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                var num = T(n.ToString(CultureInfo.InvariantCulture), 12, "AccentText", true); num.MinWidth = 16;
                line.Children.Add(num);
                line.Children.Add(T(Loc.T(key), 12));
                cz.Children.Add(line);
            }
            Cause(r.DipsVram, "Ideal.CauseVram");
            Cause(r.DipsCpu, "Ideal.CauseCpu");
            Cause(r.DipsUnclear, "Ideal.CauseNone");
            var czCard = Card(cz, "Control", 10, 12);
            czCard.Margin = new Thickness(0, 8, 0, 0);
            d.Children.Add(czCard);
            p.Children.Add(d);
        }

        var info = T(Loc.F("Game.ResultInfo", DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture), IdealMeasure.Clock(r.CleanSec),
            N(r.CleanFrames * (double)r.FgMultiplier, "#,0")), 11, "FgDim");
        info.VerticalAlignment = VerticalAlignment.Center;
        return SectionCard(0xE9D2, Loc.T("Game.ResultTitle"), null, info, p);
    }

    /// <summary>Applica un limite e lo salva nel profilo del gioco misurato (di solito quello della pagina).</summary>
    private void ApplyFor(GameInfo g, int fps)
    {
        if (_ctx != null && _ctx.Matches(g)) { SaveProfile(fps, true); return; }
        string key = Limits.Key(g.Exe);
        if (Limits.Find(_s, g) is { } f) f.Profile.Fps = fps;
        else _s.GameProfiles[key] = Limits.NewProfile(fps, g.Name, g.Path);
        _s.LimiterEnabled = true;
        _s.NotifyProfiles();
    }

    // ------------------------------------------------------------ misure salvate e confronto

    private bool _compareOpen;
    private string? _renameId;
    private readonly List<string> _cmpSel = new();

    internal void OpenCompare(string exe)
    {
        _cmpSel.Clear();
        foreach (var m in (_ctx?.Measures() ?? MeasureStore.ForGame(exe)).Take(3)) _cmpSel.Add(m.Id);
        _compareOpen = _cmpSel.Count >= 2;
        BuildCompare();
        Dispatcher.BeginInvoke(() => _compareBox.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static readonly string[] RunColors = { "Accent", "Warn", "Good" };

    private void BuildCompare()
    {
        _measuresBox.Children.Clear();
        _compareBox.Children.Clear();
        if (_ctx == null) return;
        var all = _ctx.Measures();
        _cmpSel.RemoveAll(id => all.All(m => m.Id != id));
        if (_cmpSel.Count < 2) _compareOpen = false;
        var list = new StackPanel();
        var hint = T(Loc.T("Game.MeasuresPick"), 11, "FgDim"); hint.VerticalAlignment = VerticalAlignment.Center;
        _measuresBox.Children.Add(SectionCard(0xE8AB, Loc.T("Game.Measures"), null, all.Count > 1 ? hint : null, list));
        if (all.Count == 0) { list.Children.Add(T(Loc.T("Cmp.Empty"), 12, "FgDim", wrap: true)); return; }

        var selected = all.Where(m => _cmpSel.Contains(m.Id)).OrderBy(m => _cmpSel.IndexOf(m.Id)).ToList();
        var ci = Loc.IsItalian ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo("en-US");
        foreach (var m in all)
        {
            int idx = selected.IndexOf(m);
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            string id = m.Id;
            var box = new CheckBox { IsChecked = idx >= 0, VerticalAlignment = VerticalAlignment.Center, ToolTip = Loc.T("Game.MeasuresPick") };
            AutomationProperties.SetName(box, m.Name);
            if (idx >= 0) box.SetResourceReference(Control.BorderBrushProperty, RunColors[idx]);
            box.IsEnabled = idx >= 0 || _cmpSel.Count < 3;
            box.Click += (_, _) =>
            {
                if (_cmpSel.Contains(id)) _cmpSel.Remove(id);
                else if (_cmpSel.Count < 3) _cmpSel.Add(id);
                BuildCompare();
            };
            row.Children.Add(box);
            var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            if (_renameId == id)
            {
                var tb = new TextBox { Text = m.Name, FontSize = 13 };
                tb.SetResourceReference(StyleProperty, "DarkBox");
                void Commit() { if (_renameId != id) return; m.Name = tb.Text.Trim().Length > 0 ? tb.Text.Trim() : m.Name; _renameId = null; MeasureStore.Save(); BuildCompare(); }
                tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); else if (e.Key == Key.Escape) { _renameId = null; BuildCompare(); } };
                tb.LostKeyboardFocus += (_, _) => Commit();
                tb.Loaded += (_, _) => { tb.Focus(); tb.SelectAll(); };
                mid.Children.Add(tb);
            }
            else
            {
                var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
                if (idx >= 0)
                {
                    var sq = new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
                    sq.SetResourceReference(Border.BackgroundProperty, RunColors[idx]);
                    nameRow.Children.Add(sq);
                }
                nameRow.Children.Add(T(m.Name, 13, "Fg", true));
                mid.Children.Add(nameRow);
            }
            var date = T(m.Date.ToString(Loc.IsItalian ? "d MMM, HH:mm" : "MMM d, HH:mm", ci), 11, "FgDim");
            date.Margin = new Thickness(0, 2, 0, 0);
            mid.Children.Add(date);
            Grid.SetColumn(mid, 1);
            row.Children.Add(mid);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            right.Children.Add(T(m.Recommended.ToString(CultureInfo.InvariantCulture), 16, "Fg", true));
            var u = T("FPS", 10, "FgDim"); u.Margin = new Thickness(4, 3, 10, 0);
            right.Children.Add(u);
            var ren = new Button { Content = Icon(0xE70F, 11, "FgDim"), Width = 28, Height = 28, Padding = new Thickness(0), ToolTip = Loc.T("Cmp.Rename"), Margin = new Thickness(0, 0, 4, 0) };
            ren.SetResourceReference(StyleProperty, "DarkButton");
            AutomationProperties.SetName(ren, Loc.T("Cmp.Rename"));
            ren.Click += (_, _) => { _renameId = id; BuildCompare(); };
            right.Children.Add(ren);
            var del = new Button { Content = Icon(0xE74D, 11, "FgDim"), Width = 28, Height = 28, Padding = new Thickness(0), ToolTip = Loc.T("Cmp.Delete") };
            del.SetResourceReference(StyleProperty, "DarkButton");
            AutomationProperties.SetName(del, Loc.T("Cmp.Delete"));
            del.Click += (_, _) => { MeasureStore.Remove(m); _cmpSel.Remove(id); BuildCompare(); };
            right.Children.Add(del);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);
            var card = Card(row, idx >= 0 ? "AccentSoft" : "Control", 10, 12, idx >= 0 ? "Accent" : "Line");
            card.Padding = new Thickness(10, 8, 10, 8);
            card.Margin = new Thickness(0, 0, 0, 8);
            list.Children.Add(card);
        }
        var cmp = Btn(_compareOpen ? Loc.T("Game.HideCompare") : Loc.F("Game.Compare", selected.Count), false, (_, _) =>
        {
            _compareOpen = !_compareOpen;
            BuildCompare();
            if (_compareOpen) Dispatcher.BeginInvoke(() => _compareBox.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
        }, 34, 12.5);
        cmp.IsEnabled = selected.Count >= 2;
        cmp.HorizontalAlignment = HorizontalAlignment.Left;
        cmp.Padding = new Thickness(16, 0, 16, 0);
        cmp.Margin = new Thickness(0, 4, 0, 0);
        list.Children.Add(cmp);
        if (!_compareOpen || selected.Count < 2) return;

        var body = new StackPanel();
        var graph = new FrameGraph { Height = 170, Label = Loc.T("Cmp.GraphLabel"), DurationSec = selected.Max(m => m.Seconds), Margin = new Thickness(0, 0, 0, 12) };
        for (int i = 0; i < selected.Count; i++)
            graph.Lines.Add(new FrameGraph.Series(Smooth(selected[i].Graph), (Brush)FindResource(RunColors[i])));
        body.Children.Add(graph);

        // tabella: in verde il migliore di ogni riga
        var table = new Grid();
        table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
        foreach (var _ in selected) table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        void AddRow(UIElement[] cells)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
            for (int c = 0; c < cells.Length; c++) { Grid.SetRow(cells[c], r); Grid.SetColumn(cells[c], c); table.Children.Add(cells[c]); }
            if (r > 0)
            {
                var line = Divider(); line.VerticalAlignment = VerticalAlignment.Top;
                Grid.SetRow(line, r); Grid.SetColumnSpan(line, cells.Length);
                table.Children.Add(line);
            }
            r++;
        }
        var headCells = new List<UIElement> { new TextBlock() };
        for (int i = 0; i < selected.Count; i++)
        {
            var h = new StackPanel { Orientation = Orientation.Horizontal };
            var sq = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 6, 0) };
            sq.SetResourceReference(Border.BackgroundProperty, RunColors[i]);
            h.Children.Add(sq);
            var t = T(selected[i].Name, 11, "Fg", true); t.TextTrimming = TextTrimming.CharacterEllipsis; t.MaxWidth = 90;
            h.Children.Add(t);
            headCells.Add(h);
        }
        AddRow(headCells.ToArray());
        void Metric(string label, Func<SavedMeasure, double> f, bool higher, string fmt = "0")
        {
            var vals = selected.Select(f).ToList();
            double best = higher ? vals.Max() : vals.Min();
            var cells = new List<UIElement> { Pad(T(label, 12, "FgDim")) };
            foreach (var v in vals) cells.Add(T(N(v, fmt), 13, vals.Count > 1 && Math.Abs(v - best) < 1e-6 ? "Good" : "Fg", true));
            AddRow(cells.ToArray());
        }
        static UIElement Pad(TextBlock t) { t.Margin = new Thickness(14, 0, 0, 0); return t; }
        Metric(Loc.T("Cmp.RowRec"), m => m.Recommended, true);
        Metric(Loc.T("Cmp.RowAvg"), m => m.AvgFps, true);
        Metric(Loc.T("Game.Worst"), m => m.Low1Fps, true);
        Metric(Loc.T("Cmp.RowLow01"), m => m.Low01Fps, true);
        Metric(Loc.T("Cmp.RowDips"), m => m.DipsRecommended, false, "0.#");
        var tcard = Card(table, "Control", 10, 0);
        tcard.Padding = new Thickness(0, 2, 0, 2);
        body.Children.Add(tcard);
        var bestNote = T(Loc.T("Cmp.Best"), 12, "FgDim", wrap: true);
        bestNote.Margin = new Thickness(0, 10, 0, 10);
        body.Children.Add(bestNote);
        var pickBest = selected.OrderByDescending(m => m.Recommended).ThenBy(m => m.DipsRecommended).First();
        var applyBest = Btn(Loc.F("Cmp.Apply", pickBest.Name, pickBest.Recommended), false, (_, _) =>
        {
            SaveProfile(pickBest.Recommended, true);
            ShowNote(Loc.F("Game.Applied", pickBest.Recommended, _ctx.Name));
        }, 34, 12);
        body.Children.Add(applyBest);
        _compareBox.Children.Add(SectionCard(0xE8AB, Loc.T("Game.CompareTitle"), null, null, body));
    }

    private static Border Divider()
    {
        var d = new Border { Height = 1 };
        d.SetResourceReference(Border.BackgroundProperty, "Line");
        return d;
    }

    private static float[] Smooth(float[] v, int k = 4)
    {
        var o = new float[v.Length];
        for (int i = 0; i < v.Length; i++)
        {
            double s = 0; int n = 0;
            for (int j = Math.Max(0, i - k); j <= Math.Min(v.Length - 1, i + k); j++) { if (v[j] > 0) { s += v[j]; n++; } }
            o[i] = n > 0 ? (float)(s / n) : 0;
        }
        return o;
    }
}
