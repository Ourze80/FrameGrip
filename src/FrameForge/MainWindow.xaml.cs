using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FrameGrip.Core;

namespace FrameGrip;

public partial class MainWindow : Window
{
    private const int HotkeyId = 1, RecordHotkeyId = 2, IdealHotkeyId = 3;

    private readonly Benchmark _benchmark = new();
    private readonly Settings _settings = App.StartupSettings ?? Settings.Load();   // già lette all'avvio per lingua e tema
    private readonly PresentMonitor _monitor = new();
    private readonly HardwareService _hardware = new();
    private readonly GameHook _hook = new();
    private readonly OverlayPump _pump;
    private readonly OverlayRenderer _renderer;
    private int _uiTickQueued;      // 1 se un aggiornamento dell'interfaccia è già in coda
    private string _settingsJson = "";   // impostazioni come testo, per il thread che disegna l'overlay
    private DispatcherTimer? _saveTimer;
    private long _saveOrder;
    private readonly OverlayWindow _overlay;
    private readonly int _ownPid = Environment.ProcessId;

    /// <summary>Programmi comuni che disegnano con DirectX ma non sono giochi: non vanno mai agganciati.
    /// Altri li aggiunge l'utente nel suo file delle esclusioni (vedi UserExclusions).</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "vivaldi",
        "discord", "steam", "steamwebhelper", "epicgameslauncher", "battle.net", "galaxyclient", "upc",
        "code", "devenv", "obs64", "spotify", "teams", "ms-teams", "slack", "telegram", "whatsapp",
        "framegrip", "windhawk", "cpuz", "hwinfo64", "hwmonitor", "gpu-z", "steamsysinfo",
        "vulkandriverquery", "vulkandriverquery64", "gldriverquery", "gldriverquery64", "applicationframehost", "systemsettings", "searchhost", "shellexperiencehost", "startmenuexperiencehost",
        "textinputhost", "windowsterminal", "powershell", "pwsh", "cmd", "conhost", "taskmgr", "lockapp",
        "nvidia app", "nvidia overlay", "nvcontainer", "radeonsoftware",
    };

    // elenco di tutti i sensori
    private readonly ObservableCollection<HardwareNode> _nodes = new();
    private readonly Dictionary<string, SensorNode> _nodeById = new();
    private int _treeVersion = -1;

    // voci personalizzabili dell'overlay
    private readonly ObservableCollection<EntryBase> _entries = new();
    private readonly Dictionary<string, SensorEntry> _entryById = new();
    private string _entrySignature = "";

    private IntPtr _hwnd;
    private int _targetPid;
    private string _targetName = "";
    private string _targetTitle = "";   // nome leggibile del gioco, dalle informazioni del suo eseguibile
    // Il programma di cui l'overlay mostra nome e frame: di solito quello in primo piano (_targetPid),
    // ma un gioco agganciato che disegna ancora l'overlay resta mostrato anche se il primo piano passa altrove.
    private int _shownPid;
    private string _shownName = "";
    private string _shownTitle = "";
    private int _tick;
    private TrayIcon? _tray;
    private bool _exiting;          // vero quando si esce davvero (menu dell'icona, spegnimento di Windows)
    private int _steadySeconds;    // da quanti secondi di fila il programma in primo piano disegna di continuo
    private string? _skipReason;    // perché il programma in primo piano non viene agganciato (null = nessun motivo)
    private int _capturing;         // 0 = no, altrimenti l'id del tasto che si sta scegliendo
    private bool _capturingHotkey => _capturing != 0;
    private List<int> _hotkeyBusy = new();   // tasti già presi da altri programmi (il messaggio si traduce quando lo si mostra)
    private readonly LocText? _vulkanError;
    private readonly bool _ownsVulkanLayer;
    private Func<string>? _benchMessage;   // ultimo messaggio del benchmark, rifatto se cambia la lingua

    // Pagina "FPS e benchmark": profili dei giochi e misura del limite ideale
    private readonly IdealMeasure _ideal = new();
    private readonly FpsPage _fpsPage;
    private readonly GamesPage _games;
    private readonly SensorsCurated _curated;
    // anteprime verticali (pagina Overlay e pagina Sensori): stesso componente della guida iniziale
    private readonly OverlayView Preview = new();
    private readonly OverlayView _sensView = new();
    private PreviewScene _ovlScene = null!, _sensScene = null!;
    private bool _sensNarrow;
    private bool _allSensorsOpen, _compact;
    private HotkeysPanel? _hotkeys;
    private readonly Dictionary<int, Func<string>> _hotkeyNote = new();   // avvisi delle righe dei tasti (si traducono quando si mostrano)
    private Func<string>? _flash;
    private int _flashKind;
    private long _flashUntil;
    private IdealState _lastIdealState;
    private bool _bannerOnly;
    private readonly Onboarding _guide;
    private FrameSnapshot? _lastFrames;   // frame del gioco mostrato, per i numeri dal vivo della pagina Giochi
    private GameInfo? _game;               // il gioco agganciato mostrato (aggiornato una volta al secondo)
    private int _gamePid;
    private IntPtr _gameHwnd;
    private WindowMode _gameMode;          // l'ultimo modo visto mentre il gioco era in primo piano
    private string _lastSeenExe = "";
    private string? _gamePath;             // percorso dell'eseguibile del gioco mostrato
    private int _libCheckPid;              // ultimo processo controllato nella libreria dei giochi installati
    private bool _libCheckResult;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _settings;
        Preview.Settings = _settings;
        _sensView.Settings = _settings;
        // Pagina Overlay: l'overlay riempie il riquadro (ingrandito senza deformarsi)
        _ovlScene = new PreviewScene();
        OverlayPreviewHost.Content = _ovlScene;
        _ovlScene.Show(Preview);
        _sensScene = new PreviewScene();
        SensPreviewHost.Content = _sensScene;
        _sensScene.Show(_sensView);
        SensorTree.ItemsSource = _nodes;
        EntryList.ItemsSource = _entries;
        _games = new GamesPage(_settings, _ideal, () => _game, () => _lastFrames, () => _game != null ? _hook.GetApiName(_game.Pid) : null);
        _fpsPage = _games.Detail;
        GamesHost.Content = _games;
        // prove automatiche: la finestra nasce fuori dallo schermo, così non copre mai il desktop di chi lavora
        if (Environment.GetEnvironmentVariable("FRAMEGRIP_OFFSCREEN") == "1")
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -4000;
            Top = 50;
        }
        _curated = new SensorsCurated(_settings, CuratedChanged);
        CuratedHost.Content = _curated;
        _curated.BuildPresets(PresetPanel);
        // guida iniziale: sopra tutta la finestra; si rivede da "Guida rapida" e dalle Opzioni
        _guide = new Onboarding(_settings, _curated, () => GameLibrary.Games.Count, StyleThumb);
        Grid.SetColumnSpan(_guide, 2);
        ((Grid)Content).Children.Add(_guide);
        _guide.Finished += () => { SaveSettingsSoon(); NavGames.Focus(); };
        OptionsView.ReplayGuide += ShowGuide;
        _games.Detail.DisplaySettingsRequested += GoDisplay;
        _hotkeys = new HotkeysPanel(_settings);
        _hotkeys.Capture += id => { if (_capturing == id) EndCapture(); else BeginCapture(id); };
        _hotkeys.Clear += ClearHotkey;
        _hotkeys.Reset += ResetHotkey;
        OptionsView.HotkeysHost.Content = _hotkeys;
        _ideal.Changed += IdealStateChanged;
        if (!_settings.GuideDone) Loaded += (_, _) => ShowGuide();
        // la registrazione sta nella pagina di ogni gioco
        ((Panel)RecordPanel.Parent).Children.Remove(RecordPanel);
        RecordPanel.Visibility = Visibility.Visible;
        _fpsPage.RecordHost.Content = RecordPanel;
        _games.ViewChanged += UpdateRail;
        _ideal.Changed += () => RecordButton.IsEnabled = StatusRecord.IsEnabled = !_ideal.Busy;
        IdealDemo.Apply(_ideal, _settings, _games);
        // Giochi installati (Steam, Game Pass, GOG, Epic): nomi veri e riconoscimento automatico.
        GameLibrary.Changed += () => Dispatcher.BeginInvoke(OnLibraryChanged);
        GameLibrary.ScanInBackground(force: true);
        _hardware.SetSelected(_settings.SelectedIds);
        UpdateTreeHighlight();
        Picker.Done += () => ColorPopup.IsOpen = false;
        ColorPopup.Closed += (_, _) => Picker.End();
        Loc.LanguageChanged += OnLanguageChanged;
        Theming.Changed += UpdateTreeHighlight;
        OptionsView.Attach(_settings);
        UpdateSpots();
        NavGames.IsChecked = true;
        UpdateRail();
        UpdateRecordButton();
        UpdateStatusBar();

        _overlay = new OverlayWindow(_settings);

        _pump = new OverlayPump(_hook);
        _renderer = new OverlayRenderer(_pump);

        _settings.PropertyChanged += (_, e) =>
        {
            SaveSettingsSoon();
            if (e.PropertyName == nameof(Settings.Language)) Loc.Instance.Language = _settings.Language;
            if (e.PropertyName == nameof(Settings.AppTheme)) Theming.Apply(_settings.AppTheme, _settings.AppAccent);
            Preview.Refresh();
            _sensView.Refresh();
            if (e.PropertyName == nameof(Settings.AppAccent)) Theming.ApplyAccent(_settings.AppAccent);
            if (e.PropertyName == nameof(Settings.Scale)) { _ovlScene.Fit(); _sensScene.Fit(); }
            if (e.PropertyName == nameof(Settings.CornerIndex)) UpdateSpots();   // la grandezza non cambia la misura propria dell'anteprima
            // ogni stile ha le sue grandezze del testo: passando a un altro, A−/A+ di righe e sensori mostrano le sue
            if (e.PropertyName == nameof(Settings.Layout)) foreach (var entry in _entries) entry.RefreshSize();
            _overlay.ApplySettings();
            if (e.PropertyName == nameof(Settings.Sensors)) { _hardware.SetSelected(_settings.SelectedIds); _curated.SyncChecks(); }
            SyncEntries(_hardware.Current);
        };

        _monitor.Start();
        // Una copia di sola prova dell'interfaccia non deve toccare la registrazione Vulkan dell'app vera.
        _ownsVulkanLayer = Environment.GetEnvironmentVariable("FRAMEGRIP_NO_VULKAN") != "1";
        _vulkanError = _ownsVulkanLayer ? VulkanLayer.Register() : null;
        // Il registro resta in inglese qualunque sia la lingua dell'interfaccia.
        AppLog.Write($"Start: {Environment.ProcessPath} v{typeof(MainWindow).Assembly.GetName().Version}, admin={Environment.IsPrivilegedProcess}, " +
            $"settingsOverride={Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") ?? "no"}, vulkanLayer={(_ownsVulkanLayer ? _vulkanError?.English ?? "registered" : "skipped")}, " +
            $"frames={_monitor.ErrorText?.English ?? "ok"}");

        // Il ritmo lo dà il thread dell'overlay (timer preciso): un DispatcherTimer da 100 ms in pratica
        // scattava ogni 120-140 ms, irregolare, perché riparte solo dopo il lavoro del giro.
        _pump.Tick10Hz += () =>
        {
            if (Interlocked.Exchange(ref _uiTickQueued, 1) == 0)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
                {
                    Volatile.Write(ref _uiTickQueued, 0);
                    Tick();
                }));
        };

        // Avviata insieme a Windows: la finestra non viene mostrata affatto (vedi App.OnStartup),
        // resta solo l'icona vicino all'orologio. Prima partiva "ridotta a icona" e poi nascosta:
        // Windows la teneva ridotta anche quando WPF la credeva normale, e dall'icona si riapriva
        // soltanto la barretta del titolo in un angolo dello schermo.
    }

    /// <summary>Avviata con l'argomento dell'avvio automatico: parte nascosta nell'area di notifica.</summary>
    public static bool StartsInTray =>
        Environment.GetCommandLineArgs().Contains(Autostart.TrayArgument, StringComparer.OrdinalIgnoreCase);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Theming.ApplyTitleBar(this);
        RestorePanels();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        RegisterHotkey();
        _tray = new TrayIcon(_hwnd, "FrameGrip");
        // Il selettore dei colori resta aperto finché non si preme "Done"; si chiude da solo solo se si lascia la finestra.
        Deactivated += (_, _) => ColorPopup.IsOpen = false;
        LocationChanged += (_, _) => ColorPopup.IsOpen = false;
        Application.Current.SessionEnding += (_, _) => _exiting = true;   // allo spegnimento non si chiede nulla
    }

    // ------------------------------------------------------------ opzioni, lingua e tema

    // ------------------------------------------------------------ pagine e barra di stato

    /// <summary>Una voce della barra a sinistra: mostra la sua pagina e nasconde le altre.</summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageGames == null) return;   // durante InitializeComponent
        if (_capturingHotkey) EndCapture();   // la scelta di un tasto in corso si annulla, come con Esc
        ColorPopup.IsOpen = false;
        PageGames.Visibility = NavGames.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageOverlay.Visibility = NavOverlay.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSensors.Visibility = NavSensors.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageOptions.Visibility = NavOptions.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateRail();
        // l'elenco dei sensori si riempie solo quando la finestra è aperta: aprendo la pagina si rifà subito
        if (NavSensors.IsChecked == true) { SyncTree(_hardware.Current); SyncEntries(_hardware.Current); _curated.Sync(_hardware.Current); }
    }

    /// <summary>Titolo della pagina aperta e versione con la lingua in fondo alla barra.</summary>
    private void UpdateRail()
    {
        var (title, sub) = NavOverlay.IsChecked == true ? ("Page.Overlay", "Page.OverlaySub")
            : NavSensors.IsChecked == true ? ("Page.Sensors", "Page.SensorsSub")
            : NavOptions.IsChecked == true ? ("Options.Title", "Page.OptionsSub")
            : ("Page.Games", "Page.GamesSub");
        PageTitle.Text = Loc.T(title);
        PageSub.Text = Loc.T(sub);
        // la pagina di un gioco ha già il suo titolo (copertina e nome); ricerca e "Aggiungi" solo nell'elenco
        bool games = NavGames.IsChecked == true;
        PageHeader.Visibility = games && _games.InDetail ? Visibility.Collapsed : Visibility.Visible;
        HeaderTools.Content = games && !_games.InDetail ? _games.HeaderTools : null;
        var v = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = Loc.F("Rail.Version", v == null ? "?" : $"{v.Major}.{v.Minor}");
    }

    // ------------------------------------------------------------ pagina Overlay

    private void Spot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string t } && int.TryParse(t, out int i)) _settings.CornerIndex = i;
        UpdateSpots();
    }

    private static readonly string[] SpotNames = { "Pos.TopLeft", "Pos.TopCenter", "Pos.TopRight", "Pos.BottomLeft", "Pos.BottomRight" };

    /// <summary>Lo schermo in miniatura segue la posizione scelta (anche se cambia altrove).</summary>
    private void UpdateSpots()
    {
        int c = Math.Clamp(_settings.CornerIndex, 0, 4);
        var spots = new[] { Spot0, Spot1, Spot2, Spot3, Spot4 };
        for (int i = 0; i < spots.Length; i++) spots[i].IsChecked = i == c;
        PositionName.Text = Loc.T(SpotNames[c]);
    }

    private void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        bool open = AdvancedBody.Visibility != Visibility.Visible;
        AdvancedBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AdvancedChevron.Text = open ? "" : "";
        if (open) Dispatcher.BeginInvoke(() => AdvancedBody.BringIntoView(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void GoSensors_Click(object sender, RoutedEventArgs e) => NavSensors.IsChecked = true;

    /// <summary>"N valori mostrati in M gruppi" sotto l'anteprima.</summary>
    private void UpdateValuesCount(HardwareSnapshot hw)
    {
        var groups = OverlayModel.Groups(_settings, hw).Where(g => g.Sensors.Count > 0).ToList();
        int n = groups.Sum(g => g.Sensors.Count);
        ValuesCount.Text = Loc.F(groups.Count == 1 ? "Ovl.CountOne" : "Ovl.Count", n, groups.Count);
    }

    // Il limite si sceglie nella pagina del gioco aperto (o nell'elenco, se non c'è un gioco aperto).
    private void LimitChip_Click(object sender, RoutedEventArgs e)
    {
        NavGames.IsChecked = true;
        _games.OpenRunning();
    }

    // "Giochi" nella barra riporta sempre all'elenco, anche dalla pagina di un gioco.
    private void NavGames_Click(object sender, RoutedEventArgs e) => _games.ShowHome();

    // Gli avvisi dei sensori (driver) si risolvono nella pagina Sensori; gli altri si leggono nel suggerimento.
    private void WarnChip_Click(object sender, RoutedEventArgs e)
    {
        if (DriverPanel.Visibility == Visibility.Visible) NavSensors.IsChecked = true;
    }

    private string _warnings = "", _targetLine = "";

    /// <summary>
    /// Barra di stato: il gioco in corso (o perché non è agganciato), il limite che vale adesso, la registrazione
    /// e gli avvisi. Si rifà una volta al secondo e quando cambia la lingua.
    /// </summary>
    private void UpdateStatusBar()
    {
        bool playing = _game != null;
        GameDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, playing ? "Good" : "LineStrong");
        GameRing.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, playing ? "Good" : "Line");
        GameText.Inlines.Clear();
        if (playing)
        {
            GameText.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Status.Playing") + " ") { Foreground = (Brush)FindResource("FgDim") });
            GameText.Inlines.Add(new System.Windows.Documents.Run(_game!.Name) { FontWeight = FontWeights.SemiBold });
            string api = _hook.GetApiName(_game.Pid) ?? "";
            string detail = string.Join("  ·  ", new[] { api, _game.RefreshHz > 0 ? $"{_game.RefreshHz} Hz" : "" }.Where(x => x.Length > 0));
            if (detail.Length > 0 && !_compact)
                GameText.Inlines.Add(new System.Windows.Documents.Run("   " + detail) { Foreground = (Brush)FindResource("FgDim"), FontSize = 12.5 });
        }
        else
        {
            GameText.Inlines.Add(new System.Windows.Documents.Run(Loc.T("Status.NoGame")) { Foreground = (Brush)FindResource("FgDim") });
        }
        GameText.ToolTip = _targetLine.Length > 0 ? _targetLine : null;

        var (fps, profile) = Limits.Effective(_settings, _game);
        bool on = _settings.LimiterEnabled && !_ideal.SuspendsLimiter;
        LimitChipText.Text = on ? Loc.F("Status.Limit", fps) : Loc.T("Status.LimitOff");
        LimitChipScope.Text = on ? Loc.T(profile ? "Status.LimitGame" : "Status.LimitAll") : "";
        LimitChipScope.Visibility = on && !_compact ? Visibility.Visible : Visibility.Collapsed;
        // finestra stretta: restano le icone e i valori, le spiegazioni passano nei suggerimenti
        WarnText.Visibility = StatusKeyLabel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        LimitChip.ToolTip = _compact && on ? LimitChipText.Text + " " + LimitChipScope.Text : Loc.T("Status.LimitTip");

        var lines = _warnings.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        WarnChip.Visibility = lines.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnText.Text = lines.Length switch { 0 => "", 1 => lines[0], _ => Loc.F("Status.WarnMore", lines[0], lines.Length - 1) };
        WarnChip.ToolTip = lines.Length > 0 ? _warnings : null;
        UpdateRecordButton();
    }

    /// <summary>Rifà nella lingua nuova tutto ciò che è scritto dal codice (le scritte in XAML si aggiornano da sole).</summary>
    private void OnLanguageChanged()
    {
        _entrySignature = "";   // descrizioni delle voci
        _treeVersion = -1;      // tipi dei sensori
        var hw = _hardware.Current;
        SyncTree(hw);
        SyncEntries(hw);
        _curated.Sync(hw, true);
        _curated.BuildPresets(PresetPanel);
        _guide.Relocalize();
        UpdateRail();
        UpdateStatusBar();
        UpdateSpots();
        if (_benchMessage != null) BenchText.Text = _benchMessage();
        if (_driverMessage != null) DriverText.Text = _driverMessage();
        if (DriverButton.IsEnabled && DriverPanel.Visibility == Visibility.Visible)
            DriverButton.Content = Loc.T(_hardware.DriverCode == 1060 ? "Driver.Install" : "Driver.Repair");
        _hotkeys?.Build();
        RefreshHotkeys();
        Preview.Refresh();
    }

    /// <summary>Colori di selezione dell'elenco dei sensori: li usa il modello di Windows, vanno dati secondo il tema.</summary>
    private void UpdateTreeHighlight()
    {
        var r = SensorTree.Resources;
        r[SystemColors.HighlightBrushKey] = FindResource("ControlHover");
        r[SystemColors.HighlightTextBrushKey] = FindResource("Fg");
        r[SystemColors.InactiveSelectionHighlightBrushKey] = FindResource("Control");
        r[SystemColors.InactiveSelectionHighlightTextBrushKey] = FindResource("Fg");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Solo per le prove automatiche (FRAMEGRIP_SNAPSHOT_DIR): foto della finestra disegnata dall'app stessa,
        // che funziona anche quando lo schermo è bloccato o spento e Windows non compone le finestre.
        if (msg == 0x8000 + 77 && Environment.GetEnvironmentVariable("FRAMEGRIP_SNAPSHOT_DIR") is { Length: > 0 } snapDir)
        {
            try
            {
                double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(ActualWidth * dpi), (int)(ActualHeight * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
                bmp.Render(this);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var f = System.IO.File.Create(System.IO.Path.Combine(snapDir, $"snap-{wParam.ToInt32()}.png"));
                enc.Save(f);
            }
            catch (Exception ex) { AppLog.Write("Snapshot: " + ex.Message); }
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == Native.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyId) _settings.OverlayEnabled = !_settings.OverlayEnabled;
            else if (id == RecordHotkeyId) ToggleBenchmark();
            else if (id == IdealHotkeyId) ToggleIdealByKey();
            handled = true;
        }
        else if (msg == TrayIcon.CallbackMessage)
        {
            int mouse = lParam.ToInt32();
            if (mouse == TrayIcon.WM_LBUTTONUP || mouse == TrayIcon.WM_LBUTTONDBLCLK) ShowFromTray();
            else if (mouse == TrayIcon.WM_RBUTTONUP) ShowTrayMenu();
            handled = true;
        }
        else if (msg == TrayIcon.TaskbarCreated)
        {
            _tray?.Add();
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------ salvataggio delle impostazioni

    /// <summary>
    /// Salva poco dopo l'ultima modifica, scrivendo il file fuori dal thread dell'interfaccia: trascinando un
    /// cursore prima si scriveva su disco a ogni passo, dentro l'interfaccia.
    /// </summary>
    private void SaveSettingsSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer!.Stop();
                string json = _settings.ToJson();
                long order = ++_saveOrder;
                Task.Run(() => Settings.SaveJson(json, order));
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettingsNow()
    {
        _saveTimer?.Stop();
        Settings.SaveJson(_settings.ToJson(), ++_saveOrder);
    }

    // ------------------------------------------------------------ ciclo principale

    private void Tick()
    {
        UpdateTarget();

        var hw = _hardware.Current;
        // Il limite del profilo del gioco, se c'è, altrimenti quello generale; sospeso durante la misura.
        int limit = Limits.Effective(_settings, _game).Fps;
        _pump.Configure(_settings.LimiterEnabled && !_ideal.SuspendsLimiter, limit, _settings.OverlayEnabled || _bannerOnly);   // il riquadro del giro prima
        _hook.HdrWhiteNits = (float)_settings.HdrWhiteNits;   // la DLL lo usa solo nei giochi HDR
        UpdateShown();
        // Frame del programma in primo piano: servono a capire se è un gioco da agganciare.
        var etwFrames = _targetPid != 0 ? _monitor.GetSnapshot(_targetPid) : null;
        // La misura fatta dentro il gioco è la più precisa: se c'è, ha la precedenza.
        int shown = _shownPid;
        var frames = _lastFrames = (shown != 0 ? _hook.GetSnapshot(shown) : null)
            ?? (shown == _targetPid ? etwFrames : shown != 0 ? _monitor.GetSnapshot(shown) : null);
        string? rec = _benchmark.IsRecording ? _benchmark.Elapsed.ToString(@"m\:ss") : null;
        string? api = shown != 0 ? _hook.GetApiName(shown) : null;
        _ideal.Tick(_tick % 10 == 9 && _ideal.State == IdealState.Measuring ? LoadNow(hw) : null, _settings.IdealSound);
        string? banner = _ideal.Banner(out int bannerKind) ?? FlashText(out bannerKind);
        bool bannerOnly = !_settings.OverlayEnabled && (banner != null || rec != null);
        bool showOverlay = _settings.OverlayEnabled || bannerOnly;
        _bannerOnly = bannerOnly;
        var (effLimit, effProfile) = Limits.Effective(_settings, _game);
        var data = new OverlayData(hw, frames, rec, api, api != null ? _shownTitle : null)
        {
            Banner = banner, BannerKind = bannerKind, BannerOnly = bannerOnly,
            Limit = _settings.LimiterEnabled && !_ideal.SuspendsLimiter ? effLimit : null, LimitProfile = effProfile,
        };
        SystemInfo.SetGame(shown, api);   // il gruppo "Info": API e tempo di gioco del gioco agganciato
        // L'anteprima si aggiorna solo se si vede: anche in una finestra nascosta WPF misura e disegna
        // tutto quello che si invalida (era la voce più cara dell'app chiusa nell'area di notifica).
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            if (PageOverlay.Visibility == Visibility.Visible) Preview.Update(data);
            if (PageSensors.Visibility == Visibility.Visible && _sensView.IsVisible) _sensView.Update(data);
        }
        if (_guide.IsVisible) _guide.Preview.Update(data);

        // Se la DLL disegna già l'overlay dentro un gioco, la finestra esterna sarebbe un doppione:
        // UpdateShown sceglie quel gioco ogni volta che c'è, quindi basta guardare lui.
        bool inGame = shown != 0 && _hook.IsDrawing(shown);
        bool wantWindow = showOverlay && !inGame;
        if (wantWindow != _overlay.IsVisible)
        {
            if (wantWindow) _overlay.Show(); else _overlay.Hide();
        }
        if (_overlay.IsVisible) _overlay.View.Update(data);

        if (showOverlay && _hook.HasLinks)
        {
            // L'immagine per il gioco si disegna su un altro thread (OverlayRenderer), con una copia delle
            // impostazioni rifatta solo quando cambiano; la linea del grafico la aggiunge OverlayPump.
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _settingsJson = _settings.ToJson();
            var source = shown != 0 ? _hook.GetStats(shown) ?? _monitor.GetStats(shown) : null;
            _renderer.Request(_settingsJson, data, dpi, _settings.Corner, (int)(16 * dpi), source);
        }

        // Nessuno mostra i sensori (finestra nascosta, nessun overlay né sul desktop né in un gioco):
        // si leggono più di rado, così l'app nell'area di notifica non pesa.
        _hardware.Idle = (!IsVisible || WindowState == WindowState.Minimized) && !_overlay.IsVisible
            && !(_settings.OverlayEnabled && _hook.HasLinks) && !_benchmark.IsRecording;

        if (++_tick % 10 != 0) return; // il resto una volta al secondo

        // Con la finestra ridotta a icona si leggono solo i sensori che servono all'overlay.
        bool minimized = WindowState == WindowState.Minimized || !IsVisible;
        _hardware.PollAll = !minimized || _ideal.Busy;   // durante la misura servono GPU, VRAM e core
        UpdateGame(api);
        _games.Refresh();
        RecordButton.IsEnabled = StatusRecord.IsEnabled = !_ideal.Busy;
        if (!_settings.DefaultsApplied && hw.All.Count > 0) ApplyDefaultSensors(hw);
        if (!minimized)
        {
            UpdateDriverPanel();
            SyncTree(hw);
            SyncEntries(hw);
            if (NavSensors.IsChecked == true) _curated.Sync(hw);
            if (_guide.IsVisible) { _curated.Sync(hw); _guide.UpdateFound(); }
            UpdateValuesCount(hw);
        }
        // Solo per le prove (FRAMEGRIP_LAYOUT_BENCH): tempi e immagini di tutti gli stili, poi l'app si chiude.
        if (LayoutBench.Folder != null && hw.All.Count > 20) LayoutBench.Run(_settings, hw);
            if (LayoutBench.SizeTestFile != null && hw.All.Count > 20) LayoutBench.SizeTest(_settings, hw);
            if (LayoutBench.VerticalFolder != null && hw.All.Count > 20) LayoutBench.VerticalTest(_settings, hw);
            if (LayoutBench.VisibilityFolder != null && hw.All.Count > 20) LayoutBench.VisibilityTest(_settings, hw);
            if (LayoutBench.GridShotFolder != null && hw.All.Count > 20) LayoutBench.GridShot(_settings, hw);

        if (_benchmark.IsRecording)
        {
            foreach (var g in OverlayModel.Groups(_settings, hw))
                foreach (var (item, r) in g.Sensors)
                    _benchmark.Sample(r.Id, $"{g.DefaultLabel} · {(string.IsNullOrWhiteSpace(item.Name) ? r.Name : item.Name)}", r.Type, r.Value);
            string game = _benchmark.Game, time = _benchmark.Elapsed.ToString(@"m\:ss");
            SetBenchMessage(() => Loc.F("Bench.RecordingTime", game, time));
        }

        // Un gioco disegna di continuo; un programma normale solo quando cambia qualcosa.
        _steadySeconds = etwFrames != null && etwFrames.Fps >= GameDetector.MinFps ? _steadySeconds + 1 : 0;
        bool hooked = UpdateHook(_steadySeconds >= GameDetector.SteadySeconds);
        _monitor.Prune();
        _overlay.KeepOnTop();

        _warnings = string.Join("\n",
            new[] { _monitor.Error, _hardware.Error, HotkeyError, _vulkanError?.ToString(), DriverPanel.Visibility == Visibility.Visible ? DriverText.Text : null }
                .Where(x => !string.IsNullOrEmpty(x) && !(_hideAdminBanner && IsAdminNotice(x))));
        _targetLine = _shownPid == 0
            ? Loc.T("Target.None")
            : Loc.F("Target.Active", _shownName) + (inGame ? Loc.T("Target.InGame")
                : hooked ? Loc.T("Target.Hooked") : frames == null ? Loc.T("Target.NoFrames")
                : _skipReason != null ? Loc.F("Target.NotHooked", Loc.T(_skipReason)) : "");
        UpdateStatusBar();
    }

    // Solo per le foto automatiche (FRAMEGRIP_SNAPSHOT_DIR, che non vale mai per chi usa l'app): la shell delle prove
    // non è amministratore, e gli avvisi "servono i privilegi di amministratore" coprirebbero le foto per il README. Nient'altro cambia.
    private static readonly bool _hideAdminBanner = Environment.GetEnvironmentVariable("FRAMEGRIP_SNAPSHOT_DIR") is { Length: > 0 };

    // il testo del driver può avere una frase in più ("tutto il resto funziona anche senza"): conta l'inizio
    private static bool IsAdminNotice(string text) =>
        new[] { "Frames.NeedAdmin", "Driver.NeedAdmin", "Driver.RunAsAdmin" }.Any(k => text.StartsWith(Loc.T(k), StringComparison.Ordinal));

    /// <summary>Segue la finestra in primo piano, ignorando FrameGrip stesso.</summary>
    // Il nome del gioco come lo ha scritto chi lo ha pubblicato (es. "Cyberpunk 2077" invece di
    // "Cyberpunk2077"); se l'eseguibile non lo dichiara, resta il nome del processo.
    private static string GameTitle(int pid, string fallback)
    {
        // un gioco della libreria di un negozio ha il suo nome vero (es. "Phantom Blade Zero" invece di "PBZ")
        if (GameLibrary.Find(GameLibrary.ProcessPath(pid)) is { } lib) return lib.Name;
        try
        {
            using var p = Process.GetProcessById(pid);
            var info = p.MainModule?.FileVersionInfo;
            string? name = info?.ProductName;
            if (string.IsNullOrWhiteSpace(name)) name = info?.FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { /* processo protetto o appena chiuso */ }
        return fallback;
    }

    private static readonly string? _forcedTarget = Environment.GetEnvironmentVariable("FRAMEGRIP_TARGET_PID");

    private void UpdateTarget()
    {
        // Solo per le prove automatiche: segue un processo preciso invece della finestra in primo piano,
        // così la prova non deve rubare il primo piano a chi sta usando il PC (e funziona anche a schermo bloccato).
        uint pid = 0;
        if (!uint.TryParse(_forcedTarget, out pid))
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(fg, out pid);
        }
        if (pid == 0 || pid == _ownPid || pid == _targetPid) return;

        _targetPid = (int)pid;
        _steadySeconds = 0;
        _skipReason = null;
        try { _targetName = Process.GetProcessById(_targetPid).ProcessName; }
        catch { _targetName = $"PID {_targetPid}"; }
        _targetTitle = GameTitle(_targetPid, _targetName);
    }

    /// <summary>
    /// Sceglie il programma che l'overlay mostra. Il primo piano non basta: con il gioco a schermo
    /// intero su un monitor, basta chiudere FrameGrip nell'area di notifica o cliccare il desktop
    /// sull'altro (e poi giocare col controller, senza ridare il primo piano al gioco) perché il
    /// primo piano passi a un programma qualunque. Prima l'overlay nel gioco diventava "Nessun gioco"
    /// senza FPS e in più compariva la finestra esterna sopra il gioco: due overlay sovrapposti.
    /// Ora un gioco agganciato che sta ancora disegnando l'overlay resta mostrato finché il primo
    /// piano non va a un altro gioco agganciato.
    /// </summary>
    private void UpdateShown()
    {
        int pid = _targetPid;
        if (pid == 0 || !_hook.IsHooked(pid))
        {
            int drawing = _hook.DrawingPid(_shownPid);
            if (drawing != 0) pid = drawing;
        }
        if (pid == _shownPid) return;

        _shownPid = pid;
        if (pid == _targetPid)
        {
            _shownName = _targetName;
            _shownTitle = _targetTitle;
        }
        else
        {
            try { _shownName = Process.GetProcessById(pid).ProcessName; }
            catch { _shownName = $"PID {pid}"; }
            _shownTitle = GameTitle(pid, _shownName);
        }
    }

    /// <summary>Aggancia il gioco in primo piano quando servono overlay o limitatore.</summary>
    private bool UpdateHook(bool targetPresentsFrames)
    {
        bool hooked = _targetPid != 0 && _hook.IsHooked(_targetPid);
        bool needed = _settings.OverlayEnabled || _settings.LimiterEnabled || _ideal.State != IdealState.Idle;
        if (needed && _targetPid != 0 && !hooked && !NotGames.Contains(_targetName) && !UserExclusions.NotGames.Contains(_targetName))
        {
            // I giochi scelti a mano dall'utente (elenco "Games" delle impostazioni) passano sempre.
            bool chosen = _settings.Games.Contains(_targetName, StringComparer.OrdinalIgnoreCase);
            // Un eseguibile dentro la cartella di un gioco installato da un negozio è un gioco: non serve aspettare
            // che disegni di continuo (gli altri controlli, finestra e primo piano, valgono comunque).
            bool library = IsLibraryGame(_targetPid, _targetName);
            if (chosen || library || targetPresentsFrames)
            {
                IntPtr fg = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(fg, out uint fgPid);
                if (_forcedTarget != null && fgPid != _targetPid)
                {
                    // solo per le prove (FRAMEGRIP_TARGET_PID): il gioco di prova non ha il primo piano, vale la sua finestra
                    try { using var tp = Process.GetProcessById(_targetPid); fg = tp.MainWindowHandle; fgPid = (uint)_targetPid; } catch { }
                }
                string? before = _skipReason;
                // Il motivo è una chiave di traduzione: nel registro va in inglese, a schermo nella lingua scelta.
                _skipReason = chosen ? null : fgPid != _targetPid ? "Skip.NotForeground" : GameDetector.WhyNot(_targetPid, fg);
                // solo per le prove: i giochi di prova fuori schermo hanno di proposito una finestra "strumento"
                if (_forcedTarget != null && _skipReason == "Skip.ToolWindow") _skipReason = null;
                if (_skipReason != null && _skipReason != before)
                    AppLog.Write($"Hook: {_targetName} (pid {_targetPid}) not hooked: {Loc.En(_skipReason)}");
                if (_skipReason == null)
                {
                    _hook.EnsureHooked(_targetPid);
                    hooked = _hook.IsHooked(_targetPid);
                }
            }
        }

        _fpsPage.StatusText.Text = _ideal.SuspendsLimiter ? Loc.T("Ideal.Suspended") : !_settings.LimiterEnabled
            ? Loc.T("Limiter.Applies")
            : hooked
                ? Loc.F("Limiter.ActiveOn", _targetName)
                : _hook.Error ?? Loc.T("Limiter.Waiting");
        return hooked;
    }

    /// <summary>
    /// Il gioco agganciato per la pagina FPS: eseguibile, nome, frequenza e modo dello schermo, frame generation.
    /// Il modo si legge solo quando il gioco è in primo piano (con FrameGrip davanti non si saprebbe dire) e resta.
    /// </summary>
    private void UpdateGame(string? api)
    {
        int pid = _shownPid;
        if (pid == 0 || (api == null && !_hook.IsHooked(pid))) { _game = null; return; }
        if (pid != _gamePid) { _gamePid = pid; _gameHwnd = IntPtr.Zero; _gameMode = WindowMode.Unknown; _gamePath = GameLibrary.ProcessPath(pid); }
        IntPtr fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out uint fgPid);
        if (fgPid == pid) { _gameHwnd = fg; _gameMode = DisplayInfo.Mode(fg, true); }
        else if (_gameHwnd == IntPtr.Zero)
        {
            try { using var p = Process.GetProcessById(pid); _gameHwnd = p.MainWindowHandle; } catch { }
            if (_gameHwnd != IntPtr.Zero && _gameMode == WindowMode.Unknown) _gameMode = DisplayInfo.Mode(_gameHwnd, false);
        }
        int hz = _gameHwnd != IntPtr.Zero ? DisplayInfo.RefreshHz(_gameHwnd) : 0;
        var stats = _hook.GetStats(pid) ?? _monitor.GetStats(pid);
        string exe = (_shownName + ".exe").ToLowerInvariant();
        string name = string.IsNullOrWhiteSpace(_shownTitle) ? _shownName : _shownTitle;
        var lib = GameLibrary.Find(_gamePath);
        _game = new GameInfo(pid, exe, name, hz, _gameMode, stats, stats?.FrameGenMultiplier ?? 1, _gamePath, lib?.Store, lib?.AppId);

        // Cronologia dei giochi agganciati: serve a "Aggiungi un gioco" nei profili.
        if (exe != _lastSeenExe)
        {
            _lastSeenExe = exe;
            var seen = _settings.GameHistory.FirstOrDefault(h => string.Equals(h.Exe, exe, StringComparison.OrdinalIgnoreCase));
            if (seen == null) _settings.GameHistory.Add(seen = new GameSeen { Exe = exe });
            seen.Name = name;
            seen.Store = lib?.Store;
            // gioco agganciato che la libreria non conosce: forse è appena stato installato
            if (lib == null && GameLibrary.Ready) GameLibrary.ScanInBackground();
            seen.LastSeen = DateTime.Now;
            if (_settings.GameHistory.Count > 50)
                _settings.GameHistory.Remove(_settings.GameHistory.OrderBy(h => h.LastSeen).First());
            if (_settings.GameProfiles.TryGetValue(exe, out var prof))
            {
                prof.LastSeen = DateTime.Now;
                if (lib != null) { prof.DisplayName = lib.Name; prof.Store = lib.Store; prof.AppId = lib.AppId; prof.Folder = lib.Folder; }
            }
            SaveSettingsSoon();
        }
    }

    private bool IsLibraryGame(int pid, string name)
    {
        if (pid == _libCheckPid) return _libCheckResult;
        _libCheckPid = pid;
        string n = name.ToLowerInvariant();
        bool tool = GameLibrary.IsLauncher(name) || n.Contains("launcher") || n.Contains("crash") || n.Contains("setup") || n.Contains("unins");
        var lib = tool ? null : GameLibrary.Find(GameLibrary.ProcessPath(pid));
        _libCheckResult = lib != null;
        if (lib != null) AppLog.Write($"Library: {name} (pid {pid}) is {lib.Name} ({lib.Store})");
        return _libCheckResult;
    }

    /// <summary>La libreria dei giochi è stata letta di nuovo: nomi e riconoscimento si rifanno.</summary>
    private void OnLibraryChanged()
    {
        _libCheckPid = 0;
        if (_targetPid != 0) _targetTitle = GameTitle(_targetPid, _targetName);
        if (_shownPid != 0) _shownTitle = _shownPid == _targetPid ? _targetTitle : GameTitle(_shownPid, _shownName);
        _lastSeenExe = "";
        _games.LibraryChanged();
    }

    /// <summary>Un campione di carico per la misura: GPU, VRAM occupata e il core della CPU più carico.</summary>
    private static LoadSample LoadNow(HardwareSnapshot hw)
    {
        float gpu = 0, vramMb = 0, core = 0;
        foreach (var r in hw.All)
        {
            if (r.Value is not { } v) continue;
            bool isGpu = r.RootId.Contains("gpu", StringComparison.OrdinalIgnoreCase);
            if (isGpu && r.Type == "Load" && r.Name == "GPU Core") gpu = Math.Max(gpu, v);
            else if (isGpu && r.Type == "SmallData" && r.Name == "GPU Memory Used") vramMb = Math.Max(vramMb, v);
            else if (r.Type == "Load" && r.Name.StartsWith("CPU Core #", StringComparison.Ordinal)) core = Math.Max(core, v);
        }
        double total = SystemInfo.VramTotalMb ?? 0;
        return new LoadSample(0, gpu, total > 0 ? (float)(vramMb / total * 100) : 0, core);
    }

    // ------------------------------------------------------------ tasti rapidi (funzionano anche dentro i giochi)

    /// <summary>
    /// Registra i tre tasti. RegisterHotKey vale in tutto il sistema, anche con un gioco a schermo intero in primo piano;
    /// se un altro programma ha già preso la stessa combinazione Windows dice di no e la riga lo mostra.
    /// </summary>
    private void RegisterHotkey()
    {
        if (_hwnd == IntPtr.Zero) return;
        var busy = new List<int>();
        foreach (int id in HotkeysPanel.Ids)
        {
            Native.UnregisterHotKey(_hwnd, id);
            var (mods, vk, _) = _settings.Key(id);
            if (vk == 0 || _capturing != 0) continue;   // disattivato, oppure si sta scegliendo un tasto
            if (!Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, (uint)vk)) busy.Add(id);
        }
        _hotkeyBusy = busy;
        RefreshHotkeys();
    }

    private string? HotkeyError => _hotkeyBusy.Count == 0 ? null
        : Loc.F("Hotkey.Busy", string.Join(", ", _hotkeyBusy.Select(id => _settings.Key(id).Name)));

    private void RefreshHotkeys() => _hotkeys?.Refresh(_capturing, id =>
        _hotkeyNote.TryGetValue(id, out var n) ? n() : _hotkeyBusy.Contains(id) ? Loc.T("Keys.Busy") : null);

    private void BeginCapture(int id)
    {
        _capturing = id;
        _hotkeyNote.Remove(id);
        // I tasti attuali vengono liberati: così si può riscegliere anche la stessa combinazione.
        foreach (int k in HotkeysPanel.Ids) Native.UnregisterHotKey(_hwnd, k);
        RefreshHotkeys();
    }

    /// <summary>Fine della scelta del tasto: i tasti salvati si registrano di nuovo.</summary>
    private void EndCapture()
    {
        int id = _capturing;
        _capturing = 0;
        RegisterHotkey();
        if (id != 0) _hotkeys?.FocusKey(id);
    }

    private void ClearHotkey(int id)
    {
        if (_capturing != 0) EndCapture();
        _hotkeyNote.Remove(id);
        _settings.SetKey(id, 0, 0, "—");
        RegisterHotkey();
    }

    private void ResetHotkey(int id)
    {
        if (_capturing != 0) EndCapture();
        var (mods, vk, name) = Settings.DefaultKey(id);
        _hotkeyNote.Remove(id);
        // il tasto di fabbrica potrebbe essere già usato da un'altra riga
        if (OwnerOf(id, mods, vk) is int other)
        {
            string otherTitle = KeyTitle(other);
            _hotkeyNote[id] = () => Loc.F("Keys.Twice", otherTitle);
            RefreshHotkeys();
            return;
        }
        _settings.SetKey(id, mods, vk, name);
        RegisterHotkey();
    }

    private int? OwnerOf(int id, uint mods, int vk)
    {
        foreach (int k in HotkeysPanel.Ids)
            if (k != id && _settings.Key(k) is var o && o.Vk == vk && o.Mods == mods) return k;
        return null;
    }

    private static string KeyTitle(int id) => Loc.T(id switch { 1 => "Keys.Overlay", 2 => "Keys.Record", _ => "Keys.Ideal" });

    /// <summary>Da Overlay → Avanzate e dalla pagina del gioco: apre le Opzioni sui tasti rapidi.</summary>
    private void GoHotkeys_Click(object sender, RoutedEventArgs e)
    {
        NavOptions.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
        {
            OptionsView.HotkeysTitle.BringIntoView(new Rect(0, 0, 10, 400));
            _hotkeys?.FocusKey(1);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Dalla pagina di un gioco ("Cambia" accanto alla riga dello schermo) alla sezione Schermo delle Opzioni.
    private void GoDisplay()
    {
        NavOptions.IsChecked = true;
        Dispatcher.BeginInvoke(() =>
        {
            OptionsView.DisplaySection.BringIntoView(new Rect(0, 0, 10, 300));
            OptionsView.FocusVrr();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Tasti che da soli (senza Ctrl, Alt, Shift o Win) non rubano niente alla scrittura: F1-F24, Pausa, Bloc Scorr, Ins.
    private static bool AloneOk(Key key) => key is >= Key.F1 and <= Key.F24 or Key.Pause or Key.Scroll or Key.Insert;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_dragging && e.Key == Key.Escape)
        {
            FinishDrag(commit: false);   // Esc annulla il trascinamento: la riga torna al suo posto
            e.Handled = true;
            return;
        }
        if (!_capturingHotkey) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return; // si aspetta il tasto vero e proprio

        int id = _capturing;
        if (key == Key.Escape) { EndCapture(); return; }
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            _capturing = 0;
            ClearHotkey(id);   // Canc o Backspace: nessun tasto
            _hotkeys?.FocusKey(id);
            return;
        }

        var mods = Keyboard.Modifiers;
        uint m = 0;
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) { m |= Native.MOD_CONTROL; parts.Add("Ctrl"); }
        if (mods.HasFlag(ModifierKeys.Shift)) { m |= Native.MOD_SHIFT; parts.Add("Shift"); }
        if (mods.HasFlag(ModifierKeys.Alt)) { m |= Native.MOD_ALT; parts.Add("Alt"); }
        if (mods.HasFlag(ModifierKeys.Windows)) { m |= Native.MOD_WIN; parts.Add("Win"); }
        parts.Add(KeyName(key));
        int vk = KeyInterop.VirtualKeyFromKey(key);
        string name = string.Join(" + ", parts);

        if (m == 0 && !AloneOk(key))
        {
            // una lettera o una freccia da sola smetterebbe di funzionare in tutti gli altri programmi
            _hotkeyNote[id] = () => Loc.F("Keys.NeedMod", name);
        }
        else if (OwnerOf(id, m, vk) is int other)
        {
            string otherTitle = KeyTitle(other);
            _hotkeyNote[id] = () => Loc.F("Keys.Twice", otherTitle);
        }
        else if (Native.RegisterHotKey(_hwnd, 99, m | Native.MOD_NOREPEAT, (uint)vk))
        {
            Native.UnregisterHotKey(_hwnd, 99);   // prova riuscita: il tasto è libero
            _hotkeyNote.Remove(id);
            _settings.SetKey(id, m, vk, name);
        }
        else
        {
            _hotkeyNote[id] = () => Loc.F("Keys.Taken", name);
        }
        EndCapture();
    }

    // ------------------------------------------------------------ riquadri brevi nel gioco

    /// <summary>Un messaggio breve in cima all'overlay (registrazione salvata, nessun gioco...): si vede anche a overlay spento.</summary>
    private void Flash(Func<string> text, int kind, int ms = 5000)
    {
        _flash = text;
        _flashKind = kind;
        _flashUntil = Environment.TickCount64 + ms;
    }

    private string? FlashText(out int kind)
    {
        kind = _flashKind;
        return _flash != null && Environment.TickCount64 < _flashUntil ? _flash() : null;
    }

    // ------------------------------------------------------------ misura del limite ideale dal gioco

    /// <summary>
    /// Il tasto della misura premuto (di solito dentro il gioco): parte subito sul gioco agganciato, con 3 secondi
    /// per tornare a giocare e le scelte dell'ultima volta. Premuto di nuovo la ferma: con abbastanza dati dà il risultato.
    /// </summary>
    private void ToggleIdealByKey()
    {
        if (_ideal.State is IdealState.Countdown or IdealState.Measuring)
        {
            _ideal.StopByKey(_settings.IdealSound);
            return;
        }
        if (_ideal.State == IdealState.Analyzing) return;
        if (_benchmark.IsRecording) { Flash(() => Loc.T("Keys.StopRecFirst"), 0); return; }
        var g = _game;
        if (g == null || g.Stats == null) { Flash(() => Loc.T("Keys.NoGameIdeal"), 0); return; }
        // le scelte di questo gioco (quelle della sua pagina), cercate con tutti i suoi eseguibili
        var keys = GameKeys(g);
        int fg = Math.Clamp(Settings.PerGame(_settings.IdealFg, keys, 1), 1, 4);
        int minutes = Settings.PerGame(_settings.IdealMinutesByGame, keys, _settings.IdealMinutes);
        _games.OpenExe(g.Exe, g.Name);   // nella finestra si vede la stessa misura, sulla pagina del gioco
        _ideal.Start(g, minutes, _settings.VrrDisplay == true, fg, countdown: 3, fromKey: true);
    }

    private List<string> GameKeys(GameInfo g)
    {
        var keys = new List<string> { Limits.Key(g.Exe) };
        if (GameCatalog.Build(_settings, g).FirstOrDefault(i => i.Matches(g)) is { } item) { keys.Add(item.Key); keys.AddRange(item.Exes); }
        return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Risultato arrivato da una misura partita col tasto: se nel gioco non si vede niente, avvisa Windows.</summary>
    private void IdealStateChanged()
    {
        var state = _ideal.State;
        if (state == _lastIdealState) return;
        _lastIdealState = state;
        if (state != IdealState.Result || !_ideal.FromKey || _ideal.Result == null) return;
        bool visibleInGame = (_shownPid != 0 && _hook.IsDrawing(_shownPid)) || _overlay.IsVisible;
        if (!visibleInGame)
            _tray?.Notify("FrameGrip", Loc.F("Ideal.BannerDoneN", _ideal.Result.Recommended.Cap));
    }

    // ------------------------------------------------------------ benchmark

    private void RecordButton_Click(object sender, RoutedEventArgs e) => ToggleBenchmark();

    private void ToggleBenchmark()
    {
        if (!_benchmark.IsRecording && _ideal.Busy) { Flash(() => Loc.T("Keys.IdealBusy"), 0); return; }
        if (!_benchmark.IsRecording)
        {
            // Si registra il gioco che l'overlay sta mostrando.
            var inGame = _shownPid != 0 ? _hook.GetStats(_shownPid) : null;
            var etw = _shownPid != 0 ? _monitor.GetStats(_shownPid) : null;
            if (inGame == null && etw == null)
            {
                SetBenchMessage(() => Loc.T("Bench.NoGame"));
                Flash(() => Loc.T("Keys.NoGameRec"), 0);
                return;
            }
            _benchmark.Start(_shownName, inGame, etw);
            UpdateRecordButton();
            string game = _shownName;
            SetBenchMessage(() => Loc.F("Bench.Recording", game));
            return;
        }

        var result = _benchmark.Stop();
        UpdateRecordButton();
        if (result == null || result.Frames == 0)
        {
            SetBenchMessage(() => Loc.T("Bench.NoFrames"));
            Flash(() => Loc.T("Bench.NoFrames"), 0);
            return;
        }
        // Il riepilogo è lo stesso testo salvato nel file del benchmark: resta in inglese.
        string summary = $"{result.Game}\n{_benchmark.Describe(result, false)}\n\n";
        bool saved = result.CsvPath.Length > 0;
        SetBenchMessage(() => summary + (saved ? Loc.F("Bench.SavedTo", Benchmark.Folder) : Loc.T("Bench.NotSaved")));
        // anche nel gioco: com'è andata, senza dover aprire la finestra
        if (saved) Flash(() => Loc.F("Bench.SavedBanner", result.AvgFps.ToString("0", System.Globalization.CultureInfo.InvariantCulture)), 2);
        else Flash(() => Loc.T("Bench.NotSaved"), 0);
    }

    private void UpdateRecordButton()
    {
        RecordButton.Content = Loc.T(_benchmark.IsRecording ? "Bench.Stop" : "Bench.Start");
        StatusRecordText.Text = _benchmark.IsRecording
            ? Loc.F("Status.Stop", _benchmark.Elapsed.ToString(@"m\:ss")) : Loc.T("Status.Record");
    }

    /// <summary>Mostra un messaggio del benchmark e lo ricorda, così si rifà nella lingua nuova se cambia.</summary>
    private void SetBenchMessage(Func<string> message)
    {
        _benchMessage = message;
        BenchText.Text = message();
    }

    private void OpenBenchFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Benchmark.Folder);
            Process.Start(new ProcessStartInfo(Benchmark.Folder) { UseShellExecute = true });
        }
        catch { /* cartella non apribile: niente di grave */ }
    }

    private static string KeyName(Key key)
    {
        string s = key.ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1])) return s[1..];   // D1 -> 1
        return s.StartsWith("NumPad") ? "Num " + s[6..] : s;
    }

    // ------------------------------------------------------------ elenco di tutti i sensori

    /// <summary>Al primo avvio propone i sensori che quasi tutti vogliono vedere.</summary>
    private void ApplyDefaultSensors(HardwareSnapshot hw)
    {
        string? Find(string tag, string type, params string[] nameHints)
        {
            var pool = hw.All.Where(r => r.Tag == tag && r.Type == type
                && !r.GroupName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (string hint in nameHints)
            {
                var hit = pool.FirstOrDefault(r => r.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit.Id;
            }
            return nameHints.Length == 0 || nameHints[^1] == "*" ? pool.FirstOrDefault()?.Id : null;
        }

        var picks = new[]
        {
            Find("GPU", "Load", "GPU Core", "*"),
            Find("GPU", "Temperature", "GPU Core", "*"),
            Find("GPU", "SmallData", "GPU Memory Used", "Memory Used"),
            Find("GPU", "Power", "GPU Package", "*"),
            Find("CPU", "Load", "CPU Total", "*"),
            Find("CPU", "Temperature", "Tctl", "Package", "*"),
            Find("CPU", "Power", "Package"),
            Find("RAM", "Data", "Memory Used"),
        };

        foreach (string? id in picks)
            if (id != null && !_settings.HasSensor(id)) _settings.Sensors.Add(new SensorItem { Id = id });
        _settings.DefaultsApplied = true;
        _settings.NotifySensors();
        _treeVersion = -1; // ricostruisce l'elenco con le nuove spunte
    }

    private void SyncTree(HardwareSnapshot hw)
    {
        if (hw.Version == _treeVersion)
        {
            foreach (var r in hw.All)
                if (_nodeById.TryGetValue(r.Id, out var node)) node.ValueText = ValueText(r);
            return;
        }

        // aperti solo i componenti che l'utente ha aperto (salvati nelle impostazioni); gli altri, nuovi compresi, chiusi
        var expanded = _settings.TreeExpanded.ToHashSet();
        var selected = _settings.SelectedIds.ToHashSet();

        _nodes.Clear();
        _nodeById.Clear();
        var groups = new Dictionary<string, HardwareNode>();
        foreach (var r in hw.All.OrderBy(r => SensorFormat.TypeOrder(r.Type)))
        {
            if (!groups.TryGetValue(r.GroupId, out var g))
                groups[r.GroupId] = g = new HardwareNode(r.GroupId, r.Tag, r.GroupName);
            var node = new SensorNode(r.Id, r.Type, r.Name, TypeLabel(r.Type), selected.Contains(r.Id), OnSensorToggled)
            {
                ValueText = ValueText(r),
            };
            g.Children.Add(node);
            _nodeById[r.Id] = node;
        }

        // stesso ordine con cui l'hardware viene rilevato
        foreach (var r in hw.All)
        {
            if (!groups.Remove(r.GroupId, out var g)) continue;
            g.IsExpanded = expanded.Contains(g.GroupId);
            g.PropertyChanged += OnTreeNodeChanged;
            _nodes.Add(g);
        }

        _treeVersion = hw.Version;
        SensorsEmpty.Visibility = _nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    private bool _filtering;   // durante la ricerca i componenti si aprono da soli: non è una scelta dell'utente

    /// <summary>L'utente apre o chiude un componente dell'elenco: la scelta si ricorda nelle impostazioni.</summary>
    private void OnTreeNodeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_filtering || e.PropertyName != nameof(NodeBase.IsExpanded) || sender is not HardwareNode g) return;
        var list = _settings.TreeExpanded;
        bool had = list.Contains(g.GroupId);
        if (g.IsExpanded == had) return;
        if (g.IsExpanded) list.Add(g.GroupId); else list.Remove(g.GroupId);
        SaveSettingsSoon();
    }

    /// <summary>Tipo del sensore nella lingua dell'interfaccia; i tipi sconosciuti restano come li dà la libreria.</summary>
    private static string TypeLabel(string type)
    {
        string key = "Type." + type;
        string text = Loc.T(key);
        return text == key ? SensorFormat.TypeName(type) : text;
    }

    private static string ValueText(SensorReading r)
    {
        var (value, unit) = SensorFormat.Format(r);
        return unit.Length == 0 ? value : $"{value} {unit}";
    }

    private void OnSensorToggled(SensorNode node)
    {
        // Se si spunta un sensore, il suo gruppo si riapre: la riga nuova deve vedersi.
        if (node.IsSelected && _hardware.Current.All.FirstOrDefault(r => r.Id == node.Id) is { } reading)
            _collapsedGroups.Remove(reading.RootId);
        _settings.SetSensor(node.Id, node.IsSelected);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        string q = SearchBox.Text.Trim();
        _curated.Filter(q);
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllSensors();
    }

    /// <summary>L'elenco completo: si apre con il suo pulsante, e da solo durante la ricerca (così si trova tutto).</summary>
    private void AllSensorsToggle_Click(object sender, RoutedEventArgs e)
    {
        _allSensorsOpen = !_allSensorsOpen;
        UpdateAllSensors();
        if (_allSensorsOpen) Dispatcher.BeginInvoke(() => TreeBox.BringIntoView(new Rect(0, -70, TreeBox.ActualWidth, TreeBox.ActualHeight + 70)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateAllSensors()
    {
        bool open = _allSensorsOpen || SearchBox.Text.Trim().Length > 0;
        TreeBox.Visibility = AllSensorsHelp.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        AllSensorsChevron.Text = open ? "" : "";
    }

    /// <summary>Apre la guida iniziale sopra la pagina Giochi.</summary>
    private void ShowGuide()
    {
        NavGames.IsChecked = true;
        _games.ShowHome();
        _curated.Sync(_hardware.Current);
        _guide.Show();
    }

    /// <summary>Copia della miniatura di uno stile della pagina Overlay, per la guida (con i colori del tema di adesso).</summary>
    private UIElement? StyleThumb(int index) =>
        index < StyleList.Items.Count && StyleList.Items[index] is ListBoxItem { Content: Panel p }
            && p.Children.OfType<Canvas>().FirstOrDefault() is { } c
            ? (UIElement)System.Windows.Markup.XamlReader.Parse(System.Windows.Markup.XamlWriter.Save(c))
            : null;

    private void NavHelp_Click(object sender, RoutedEventArgs e) => ShowGuide();

    /// <summary>Una spunta di "Cosa mostrare": si rifanno la lista a destra e l'elenco completo.</summary>
    private void CuratedChanged()
    {
        _entrySignature = "";
        _treeVersion = -1;
        _settings.NotifySensors();
        SyncTree(_hardware.Current);
        SaveSettingsSoon();
    }

    private void ApplyFilter()
    {
        string q = SearchBox.Text.Trim();
        foreach (var hw in _nodes)
        {
            bool hwMatch = q.Length == 0 || hw.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                || hw.Tag.Contains(q, StringComparison.OrdinalIgnoreCase);
            bool any = false;
            foreach (var s in hw.Children)
            {
                bool match = hwMatch || s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || s.TypeName.Contains(q, StringComparison.OrdinalIgnoreCase);
                s.Vis = match ? Visibility.Visible : Visibility.Collapsed;
                any |= match;
            }
            hw.Vis = any ? Visibility.Visible : Visibility.Collapsed;
            _filtering = true;
            // con la ricerca si apre ciò che trova; tolta la ricerca si torna alle scelte salvate
            hw.IsExpanded = q.Length > 0 ? any : _settings.TreeExpanded.Contains(hw.GroupId);
            _filtering = false;
        }
    }

    // ------------------------------------------------------------ voci personalizzabili

    /// <summary>Ricostruisce l'elenco solo se cambia la sua struttura; altrimenti aggiorna i valori.</summary>
    private void SyncEntries(HardwareSnapshot hw)
    {
        string signature = string.Join('|', _settings.Theme, _settings.ShowFps, _settings.ShowLows,
            _settings.ShowFrametime, _settings.ShowPacing, _settings.ShowApi, hw.Version, string.Join(',', _settings.SelectedIds),
            string.Join(',', _settings.FrameItems.Select(i => i.Id)));

        if (signature == _entrySignature)
        {
            foreach (var r in hw.All)
                if (_entryById.TryGetValue(r.Id, out var entry)) entry.ValueText = ValueText(r);
            return;
        }
        _entrySignature = signature;

        var theme = Theme.Get(_settings.Theme);
        _entries.Clear();
        _entryById.Clear();
        foreach (var g in OverlayModel.Groups(_settings, hw))
        {
            var group = new GroupEntry(_settings, g.Key, true, g.DefaultLabel, g.HardwareName, theme.Label(g.Tag), OnEntryChanged);
            _entries.Add(group);
            int count = 0;
            foreach (var (item, r) in g.Sensors)
            {
                var entry = new SensorEntry(item, g.Key, r.Name, theme.Value, OnEntryChanged) { ValueText = ValueText(r) };
                _entries.Add(entry);
                _entryById[item.Id] = entry;
                count++;
            }
            InitGroupExpansion(group, count);
        }

        // Le misure dei frame: una sola riga FPS, con le sue voci personalizzabili come i sensori.
        // Si elencano tutte, anche quelle spente: ognuna ha il suo interruttore e la sua spiegazione.
        var fpsGroup = new GroupEntry(_settings, "frame:FPS", false, "FPS", "", theme.Label("FPS"), OnEntryChanged);
        _entries.Add(fpsGroup);
        int frameCount = 0;
        foreach (var item in OverlayModel.AllFrameItems(_settings))
        {
            _entries.Add(new SensorEntry(item, "frame:FPS", OverlayModel.FrameName(item.Id), theme.Value, OnEntryChanged)
            {
                IsFrame = true,
                Description = OverlayModel.FrameDescription(item.Id),
            });
            frameCount++;
        }
        InitGroupExpansion(fpsGroup, frameCount);
        _entries.Add(new ToggleEntry(Loc.T("Entry.Graph"), Loc.T("Entry.GraphDesc"),
            () => _settings.ShowGraph, v => _settings.ShowGraph = v));
        var apiGroup = new GroupEntry(_settings, "frame:API", false, Loc.T("Entry.GameName"), "", theme.Label("API"), OnEntryChanged)
        {
            ShownGetter = () => _settings.ShowApi,
            ShownSetter = v => _settings.ShowApi = v,
            Description = Loc.T("Entry.GameNameDesc"),
        };
        _entries.Add(apiGroup);
        InitGroupExpansion(apiGroup, 0);   // nessun figlio: niente freccia
    }

    // Gruppi chiusi nella lista "Customize" (solo per la sessione, non finiscono nelle impostazioni).
    private readonly HashSet<string> _collapsedGroups = new();

    /// <summary>Applica lo stato ricordato al gruppo appena costruito e collega la freccia.</summary>
    private void InitGroupExpansion(GroupEntry group, int childCount)
    {
        group.ChildCount = childCount;
        bool collapsed = _collapsedGroups.Contains(group.Key);
        if (collapsed)
        {
            group.IsExpanded = false;   // il callback non è ancora collegato: non tocca l'insieme
            foreach (var e in _entries.OfType<SensorEntry>())
                if (e.GroupKey == group.Key) e.RowVis = Visibility.Collapsed;
        }
        group.ExpandedChanged = OnGroupExpandedChanged;
    }

    private void OnGroupExpandedChanged(string key, bool expanded)
    {
        if (expanded) _collapsedGroups.Remove(key); else _collapsedGroups.Add(key);
        var vis = expanded ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in _entries.OfType<SensorEntry>())
            if (e.GroupKey == key) e.RowVis = vis;
    }

    private void OnEntryChanged() => _settings.NotifySensors();

    private static T? EntryOf<T>(object sender) where T : class => (sender as FrameworkElement)?.DataContext as T;

    // ------------------------------------------------------------ driver dei sensori del processore

    private bool _driverBusy, _driverDone;
    private Func<string>? _driverMessage;   // testo mostrato nel riquadro del driver, rifatto se cambia la lingua

    private void SetDriverMessage(Func<string> message)
    {
        _driverMessage = message;
        DriverText.Text = message();
    }

    /// <summary>Mostra l'avviso (e il pulsante per rimediare) solo quando il driver manca o è rotto.</summary>
    private void UpdateDriverPanel()
    {
        if (_driverBusy || _driverDone) return;
        int code = _hardware.DriverCode;
        string? problem = code == -2 ? null : SensorDriver.Problem(code);
        DriverPanel.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        if (problem == null) return;
        SetDriverMessage(() => Loc.F("Driver.WithoutIt", SensorDriver.Problem(code)));
        DriverButton.Content = Loc.T(code == 1060 ? "Driver.Install" : "Driver.Repair");
        DriverButton.Visibility = code == 5 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DriverButton_Click(object sender, RoutedEventArgs e)
    {
        _driverBusy = true;
        DriverButton.IsEnabled = false;
        SetDriverMessage(() => Loc.T("Driver.Working"));
        LocText? error = await Task.Run(SensorDriver.InstallOrRepair);
        _driverBusy = false;
        DriverButton.IsEnabled = true;
        if (error == null)
        {
            _driverDone = true;
            DriverButton.Visibility = Visibility.Collapsed;
            SetDriverMessage(() => Loc.T("Driver.Done"));
        }
        else
        {
            SetDriverMessage(error.ToString);
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e) => EntryOf<EntryBase>(sender)?.EndNameEdit();

    private void SensorUp_Click(object sender, RoutedEventArgs e) => MoveSensor(EntryOf<SensorEntry>(sender), -1);
    private void SensorDown_Click(object sender, RoutedEventArgs e) => MoveSensor(EntryOf<SensorEntry>(sender), +1);

    /// <summary>Scambia il sensore con il vicino della stessa riga.</summary>
    private void MoveSensor(SensorEntry? entry, int direction)
    {
        if (entry == null) return;
        int i = SensorsOfGroup(entry).IndexOf(entry.Item);
        if (i >= 0) MoveSensorTo(entry, i + direction);
    }

    /// <summary>
    /// Le voci della stessa riga dell'overlay, nell'ordine salvato. Un sensore resta sempre nella riga
    /// del suo componente (la riga è decisa dall'hardware), quindi si riordina solo lì dentro.
    /// </summary>
    private List<SensorItem> SensorsOfGroup(SensorEntry entry) => entry.IsFrame
        ? OverlayModel.AllFrameItems(_settings)
        : _settings.Sensors.Where(s => _entryById.TryGetValue(s.Id, out var en) && en.GroupKey == entry.GroupKey).ToList();

    /// <summary>Porta il sensore alla posizione indicata dentro la sua riga (frecce e trascinamento).</summary>
    private void MoveSensorTo(SensorEntry entry, int index)
    {
        if (entry.IsFrame)
        {
            var all = OverlayModel.AllFrameItems(_settings);   // è l'elenco salvato: si modifica direttamente
            int from = all.IndexOf(entry.Item);
            if (from < 0 || index < 0 || index >= all.Count || index == from) return;
            all.RemoveAt(from);
            all.Insert(index, entry.Item);
            _settings.NotifySensors();
            return;
        }

        // Gli altri sensori sono mescolati nell'elenco salvato: si riordinano quelli della riga
        // e si rimettono negli stessi posti, così le altre righe non si muovono.
        var list = _settings.Sensors;
        var group = SensorsOfGroup(entry);
        int a = group.IndexOf(entry.Item);
        if (a < 0 || index < 0 || index >= group.Count || index == a) return;
        var slots = group.Select(s => list.IndexOf(s)).ToList();
        group.RemoveAt(a);
        group.Insert(index, entry.Item);
        for (int k = 0; k < slots.Count; k++) list[slots[k]] = group[k];
        _settings.NotifySensors();
    }

    private void SensorRemove_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf<SensorEntry>(sender) is not { } entry || entry.IsFrame) return;
        string id = entry.Item.Id;
        _settings.SetSensor(id, false);
        if (_nodeById.TryGetValue(id, out var node)) node.SetSelectedSilently(false);
    }

    private void SizeDown_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf<EntryBase>(sender) is { } entry) entry.Size -= 0.1;
    }

    private void SizeUp_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf<EntryBase>(sender) is { } entry) entry.Size += 0.1;
    }

    /// <summary>Mette come etichetta il nome del modello, senza la marca ripetuta (es. "Ryzen 7 9800X3D").</summary>
    private void UseHardwareName_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf<GroupEntry>(sender) is not { } g || g.HardwareName.Length == 0) return;
        string name = g.HardwareName;
        foreach (string prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD Radeon ", "AMD ", "Intel(R) ", "Intel " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { name = name[prefix.Length..]; break; }
        g.Text = name.Trim();
    }

    private void GroupUp_Click(object sender, RoutedEventArgs e) => MoveGroup(EntryOf<GroupEntry>(sender), -1);
    private void GroupDown_Click(object sender, RoutedEventArgs e) => MoveGroup(EntryOf<GroupEntry>(sender), +1);

    /// <summary>Sposta un'intera riga hardware prima o dopo quella vicina.</summary>
    private void MoveGroup(GroupEntry? group, int direction)
    {
        if (group == null || !group.IsHardware) return;
        int i = HardwareGroupKeys().IndexOf(group.Key);
        if (i >= 0) MoveGroupTo(group, i + direction);
    }

    private List<string> HardwareGroupKeys() => _entries.OfType<GroupEntry>().Where(g => g.IsHardware).Select(g => g.Key).ToList();

    /// <summary>Porta un'intera riga hardware alla posizione indicata tra le righe hardware (frecce e trascinamento).</summary>
    private void MoveGroupTo(GroupEntry group, int index)
    {
        if (!group.IsHardware) return;
        var keys = HardwareGroupKeys();
        int i = keys.IndexOf(group.Key);
        if (i < 0 || index < 0 || index >= keys.Count || index == i) return;
        keys.RemoveAt(i);
        keys.Insert(index, group.Key);

        string KeyOf(SensorItem item) => _entryById.TryGetValue(item.Id, out var en) ? en.GroupKey : "";
        var reordered = keys.SelectMany(k => _settings.Sensors.Where(s => KeyOf(s) == k)).ToList();
        reordered.AddRange(_settings.Sensors.Where(s => KeyOf(s).Length == 0)); // sensori di hardware non presente ora
        _settings.Sensors.Clear();
        _settings.Sensors.AddRange(reordered);
        _settings.NotifySensors();
    }

    // ------------------------------------------------------------ trascinamento delle righe "Customize"
    // Si afferra una riga (fuori da pulsanti, caselle e campi di testo) e la si lascia dove si vuole:
    // un sensore dentro la sua riga dell'overlay, una riga hardware tra le altre righe hardware.
    // Lo spostamento usa gli stessi metodi delle frecce, quindi salvataggio e overlay restano identici.

    // Mentre si trascina, la riga "si solleva": una sua immagine un po' ingrandita, con ombra e bordo
    // d'accento, segue il mouse; al suo posto resta un segnaposto sbiadito che scivola dove la riga finirà,
    // e le altre righe si spostano per fargli spazio. Tutto con RenderTransform: nessun ricalcolo
    // dell'impaginazione a ogni movimento del mouse.

    /// <summary>Un blocco che si sposta tutto insieme: una riga di sensore, o un'intera riga hardware con i suoi sensori.</summary>
    private sealed class DragBlock
    {
        public readonly List<FrameworkElement> Parts = new();
        public readonly TranslateTransform Shift = new();
        public double Top, Bottom;
        public double Height => Bottom - Top;
        public double Middle => (Top + Bottom) / 2;
    }

    private const double GhostBorder = 1.5, GhostScale = 1.05;
    private static readonly Duration SlideTime = new(TimeSpan.FromMilliseconds(140));
    private static readonly IEasingFunction SlideEase = new CubicEase { EasingMode = EasingMode.EaseOut };

    private EntryBase? _dragEntry;          // riga premuta (in attesa della soglia) o trascinata
    private Point _dragStart;
    private bool _dragging;                 // il mouse sta portando la riga
    private bool _settling;                 // la riga sta tornando al suo posto (rilasciata o annullata)
    private List<DragBlock>? _blocks;
    private int _dragFrom, _dragTarget;     // posizione di partenza e di arrivo tra i blocchi
    private Point _ghostOrigin;
    private DispatcherTimer? _dragScrollTimer;

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || _settling) return;
        _dragEntry = null;
        if (e.OriginalSource is not DependencyObject source || IsInteractive(source)) return;
        if (EntryList.ContainerFromElement(source) is not FrameworkElement container) return;
        _dragEntry = container.DataContext switch
        {
            SensorEntry s when s.RowVis == Visibility.Visible => s,
            GroupEntry g when g.IsHardware => g,
            _ => null,
        };
        _dragStart = e.GetPosition(EntryList);
        // Non si segna come gestito: un semplice clic deve restare un clic.
    }

    /// <summary>Il clic è su un controllo della riga (pulsante, casella, campo di testo...): lì non si trascina.</summary>
    private bool IsInteractive(DependencyObject? d)
    {
        while (d != null && d != EntryList)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.Thumb or System.Windows.Controls.Primitives.RangeBase or ComboBox)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragEntry == null || _settling) return;
        if (!_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { _dragEntry = null; return; }
            var d = e.GetPosition(EntryList) - _dragStart;
            // Una soglia un po' più larga di quella di sistema: un clic un po' mosso non sposta nulla.
            if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 1.5
                && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 1.5) return;
            StartDrag();
            if (!_dragging) return;
        }
        UpdateDrag(e.GetPosition(EntryList));
        e.Handled = true;
    }

    private void EntryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) { if (!_settling) _dragEntry = null; return; }
        FinishDrag(commit: true);
        e.Handled = true;
    }

    // Finestra lasciata, altro programma davanti...: il trascinamento si annulla.
    private void EntryList_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging) FinishDrag(commit: false);
    }

    private void StartDrag()
    {
        if (_dragEntry == null) return;
        var blocks = BuildDragBlocks(_dragEntry, out int from);
        if (from < 0) { _dragEntry = null; return; }
        // Si segna subito che il trascinamento è partito: prendere il mouse (CaptureMouse) fa arrivare
        // un altro movimento del mouse prima di tornare qui, che non deve far ripartire tutto da capo.
        _dragging = true;
        _blocks = blocks;
        _dragFrom = _dragTarget = from;

        // L'immagine della riga sollevata, presa prima di sbiadire il suo posto.
        var b = _blocks[_dragFrom];
        double left = _dragEntry is SensorEntry ? 44 : 0;   // i sensori sono rientrati sotto la loro riga
        double width = Math.Max(1, EntryList.ActualWidth - left);
        DragGhostImage.Source = Snapshot(new Rect(left, b.Top, width, Math.Max(1, b.Height)));
        DragGhostImage.Width = width;
        DragGhostImage.Height = Math.Max(1, b.Height);
        _ghostOrigin = new Point(left - GhostBorder, b.Top - GhostBorder);
        DragGhostMove.X = _ghostOrigin.X;
        DragGhostMove.Y = _ghostOrigin.Y;
        DragGhost.Visibility = Visibility.Visible;
        Animate(DragGhostScale, ScaleTransform.ScaleXProperty, GhostScale);
        Animate(DragGhostScale, ScaleTransform.ScaleYProperty, GhostScale);

        foreach (var block in _blocks)
            foreach (var part in block.Parts) part.RenderTransform = block.Shift;
        foreach (var part in b.Parts) part.Opacity = 0.25;   // il segnaposto

        _entries.CollectionChanged += Entries_ChangedDuringDrag;
        if (!EntryList.CaptureMouse()) { CleanupDrag(); return; }
        Mouse.OverrideCursor = Cursors.SizeAll;
        _dragScrollTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => AutoScrollDuringDrag(), Dispatcher);
        _dragScrollTimer.Start();
    }

    /// <summary>La riga segue il mouse 1:1; se cambia il punto d'arrivo, le altre righe scivolano.</summary>
    private void UpdateDrag(Point p)
    {
        if (_blocks == null) return;
        double dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        DragGhostMove.X = _ghostOrigin.X + dx;
        DragGhostMove.Y = _ghostOrigin.Y + dy;

        // Il punto d'arrivo: quante altre righe hanno il centro sopra il centro della riga sollevata.
        double center = _blocks[_dragFrom].Middle + dy;
        int target = 0;
        for (int i = 0; i < _blocks.Count; i++)
            if (i != _dragFrom && _blocks[i].Middle < center) target++;
        if (target == _dragTarget) return;
        _dragTarget = target;
        ApplyShifts(target);
    }

    /// <summary>Apre il varco al punto d'arrivo: le righe in mezzo scivolano di un posto, il segnaposto va nel varco.</summary>
    private void ApplyShifts(int target)
    {
        if (_blocks == null) return;
        double h = _blocks[_dragFrom].Height;
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (i == _dragFrom) continue;
            double to = i > _dragFrom && i <= target ? -h : i < _dragFrom && i >= target ? h : 0;
            Animate(_blocks[i].Shift, TranslateTransform.YProperty, to);
        }
        Animate(_blocks[_dragFrom].Shift, TranslateTransform.YProperty, PlaceholderOffset(target));
    }

    /// <summary>Di quanto si sposta il segnaposto per arrivare al punto d'arrivo.</summary>
    private double PlaceholderOffset(int target)
    {
        if (_blocks == null) return 0;
        double offset = 0;
        for (int i = _dragFrom + 1; i <= target && i < _blocks.Count; i++) offset += _blocks[i].Height;
        for (int i = target; i < _dragFrom; i++) offset -= _blocks[i].Height;
        return offset;
    }

    /// <summary>
    /// Rilascio (commit) o annullamento: l'immagine sollevata scende nel segnaposto (o torna al posto di partenza)
    /// e solo alla fine dell'animazione si sposta davvero la voce, con gli stessi metodi delle frecce.
    /// </summary>
    private void FinishDrag(bool commit)
    {
        if (!_dragging || _blocks == null) return;
        _dragging = false;
        _settling = true;
        _dragScrollTimer?.Stop();
        Mouse.OverrideCursor = null;
        if (EntryList.IsMouseCaptured) EntryList.ReleaseMouseCapture();

        int index = commit && _dragTarget != _dragFrom ? _dragTarget : -1;
        if (index < 0) ApplyShifts(_dragFrom);   // tutto torna com'era
        double landing = index < 0 ? 0 : PlaceholderOffset(index);
        var entry = _dragEntry;

        Animate(DragGhostScale, ScaleTransform.ScaleXProperty, 1);
        Animate(DragGhostScale, ScaleTransform.ScaleYProperty, 1);
        Animate(DragGhostMove, TranslateTransform.XProperty, _ghostOrigin.X);
        var settle = new DoubleAnimation(_ghostOrigin.Y + landing, SlideTime) { EasingFunction = SlideEase };
        settle.Completed += (_, _) =>
        {
            if (!_settling) return;   // già chiuso (l'elenco è stato ricostruito nel frattempo)
            CleanupDrag();
            if (index < 0) return;
            if (entry is SensorEntry s) MoveSensorTo(s, index);
            else if (entry is GroupEntry g) MoveGroupTo(g, index);
        };
        DragGhostMove.BeginAnimation(TranslateTransform.YProperty, settle);
    }

    /// <summary>Toglie immagine, segnaposto e spostamenti: l'elenco torna normale.</summary>
    private void CleanupDrag()
    {
        _entries.CollectionChanged -= Entries_ChangedDuringDrag;
        _dragScrollTimer?.Stop();
        if (_dragging) Mouse.OverrideCursor = null;
        _dragging = false;
        _settling = false;
        if (EntryList.IsMouseCaptured) EntryList.ReleaseMouseCapture();
        if (_blocks != null)
            foreach (var block in _blocks)
            {
                block.Shift.BeginAnimation(TranslateTransform.YProperty, null);
                foreach (var part in block.Parts)
                {
                    part.ClearValue(RenderTransformProperty);
                    part.ClearValue(OpacityProperty);
                }
            }
        _blocks = null;
        _dragEntry = null;
        DragGhost.Visibility = Visibility.Collapsed;
        DragGhostImage.Source = null;
        foreach (var dp in new[] { TranslateTransform.XProperty, TranslateTransform.YProperty })
            DragGhostMove.BeginAnimation(dp, null);
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DragGhostScale.ScaleX = DragGhostScale.ScaleY = 1;
    }

    // L'elenco è stato ricostruito sotto le mani (hardware cambiato...): le righe di prima non esistono più.
    private void Entries_ChangedDuringDrag(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => CleanupDrag();

    private static void Animate(Animatable target, DependencyProperty property, double to) =>
        target.BeginAnimation(property, new DoubleAnimation(to, SlideTime) { EasingFunction = SlideEase });

    /// <summary>
    /// I blocchi tra cui si può lasciare la riga, dall'alto in basso, con la loro posizione di partenza:
    /// i sensori della stessa riga dell'overlay, oppure le righe hardware (intestazione più i sensori visibili).
    /// </summary>
    private List<DragBlock> BuildDragBlocks(EntryBase dragged, out int from)
    {
        var blocks = new List<DragBlock>();
        from = -1;
        FrameworkElement? Container(object item) => EntryList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement;
        DragBlock? current = null;
        foreach (var entry in _entries)
        {
            bool starts = dragged is SensorEntry ds
                ? entry is SensorEntry s && s.GroupKey == ds.GroupKey
                : entry is GroupEntry { IsHardware: true };
            bool continues = dragged is GroupEntry && current != null && entry is not GroupEntry;
            if (!starts && !continues) { current = null; continue; }
            if (Container(entry) is not { ActualHeight: > 0 } c) continue;
            double top = c.TranslatePoint(new Point(0, 0), EntryList).Y;
            if (starts)
            {
                current = new DragBlock { Top = top, Bottom = top };
                if (entry == dragged) from = blocks.Count;
                blocks.Add(current);
            }
            current!.Parts.Add(c);
            current.Bottom = top + c.ActualHeight;
        }
        return blocks;
    }

    /// <summary>Immagine di una parte della lista, alla risoluzione dello schermo.</summary>
    private BitmapSource Snapshot(Rect area)
    {
        // Si disegna la lista dall'alto fino al fondo della riga e se ne copia il pezzo che serve.
        // Si fa una volta sola, all'inizio del trascinamento.
        var dpi = VisualTreeHelper.GetDpi(EntryList);
        int right = (int)Math.Ceiling(area.Right * dpi.DpiScaleX), bottom = (int)Math.Ceiling(area.Bottom * dpi.DpiScaleY);
        var full = new RenderTargetBitmap(Math.Max(1, right), Math.Max(1, bottom), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        full.Render(EntryList);
        int x = Math.Clamp((int)(area.X * dpi.DpiScaleX), 0, full.PixelWidth - 1), y = Math.Clamp((int)(area.Y * dpi.DpiScaleY), 0, full.PixelHeight - 1);
        // I pixel si copiano subito: un ritaglio "pigro" li leggerebbe dopo, con il segnaposto già sbiadito.
        int w = Math.Max(1, full.PixelWidth - x), h = Math.Max(1, full.PixelHeight - y), stride = w * 4;
        var pixels = new byte[stride * h];
        full.CopyPixels(new Int32Rect(x, y, w, h), pixels, stride, 0);
        var copy = BitmapSource.Create(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    /// <summary>Vicino al bordo alto o basso della lista, la lista scorre da sola (più veloce più ci si avvicina).</summary>
    private void AutoScrollDuringDrag()
    {
        if (!_dragging) { _dragScrollTimer?.Stop(); return; }
        const double edge = 32;
        double y = Mouse.GetPosition(EntryScroll).Y, height = EntryScroll.ViewportHeight;
        double step = y < edge ? -(edge - y) : y > height - edge ? y - (height - edge) : 0;
        if (step == 0) return;
        step = Math.Clamp(step, -edge, edge) * 0.35;
        double offset = Math.Clamp(EntryScroll.VerticalOffset + step, 0, EntryScroll.ScrollableHeight);
        if (Math.Abs(offset - EntryScroll.VerticalOffset) < 0.5) return;
        EntryScroll.ScrollToVerticalOffset(offset);
        // La riga sollevata sta nel contenuto che scorre: la si riallinea al mouse.
        UpdateDrag(Mouse.GetPosition(EntryList));
    }

    // ------------------------------------------------------------ tavolozza dei colori

    /// <summary>Apre il selettore (condiviso con le Opzioni, vedi ColorPicker) per il colore di una voce.</summary>
    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf<EntryBase>(sender) is not { } entry) return;
        Picker.Begin(hex =>
        {
            entry.Color = hex;
            return (hex ?? "", (entry.ColorBrush as SolidColorBrush)?.Color);
        }, entry.Color ?? "", (entry.ColorBrush as SolidColorBrush)?.Color ?? Colors.White);
        ColorPopup.PlacementTarget = (UIElement)sender;
        ColorPopup.IsOpen = true;
    }
    // ------------------------------------------------------------ riquadri ridimensionabili

    // Tutte le colonne sono in quote della larghezza (*): crescono con la finestra. Si salvano le quote, non i pixel.
    // Pagina Overlay: quota dell'anteprima. Pagina Sensori: quota dell'anteprima e, nel resto, quota dell'elenco dei sensori.
    private const double DefaultOvlShare = 0.34, DefaultSensShare = 0.26, DefaultTreeSplit = 0.36;
    private double _sensShare = DefaultSensShare;

    private void RestorePanels()
    {
        double ovl = _settings.OvlPreviewShare is > 0.1 and < 0.8 ? _settings.OvlPreviewShare : DefaultOvlShare;
        ColLook.Width = new GridLength(1 - ovl, GridUnitType.Star);
        ColOvlPreview.Width = new GridLength(ovl, GridUnitType.Star);
        _sensShare = _settings.SensPreviewShare is > 0.1 and < 0.8 ? _settings.SensPreviewShare : DefaultSensShare;
        // con l'anteprima accanto, l'elenco dei sensori non prende più di un terzo abbondante: i nomi nell'overlay restano leggibili
        double split = _settings.PanelSplit is > 0.05 and < 0.95 ? Math.Min(_settings.PanelSplit, DefaultTreeSplit) : DefaultTreeSplit;
        SetSensColumns(_sensShare, split);
        SizeChanged += (_, _) =>
        {
            FitPanels();
            // sotto i 1400 px la barra di stato si stringe, così il nome del gioco resta leggibile
            bool compact = ActualWidth < 1400;
            if (compact != _compact) { _compact = compact; UpdateStatusBar(); }
        };
        FitPanels();
    }

    // L'anteprima della pagina Overlay non può togliere alle impostazioni il loro spazio minimo.
    // Nella pagina Sensori, sotto i 1390 px l'anteprima diventa un pannello da aprire col pulsante "Anteprima".
    private void FitPanels()
    {
        // barra 284 + elenco dei sensori 290 + 20 + "Nell'overlay" 500 + 20 + anteprima 260 = 1374
        bool narrow = (ActualWidth > 0 ? ActualWidth : Width) < 1390;
        if (narrow != _sensNarrow) SetSensNarrow(narrow);
    }

    /// <summary>Colonne della pagina Sensori: share = quota dell'anteprima, split = quota dell'elenco nel resto.</summary>
    private void SetSensColumns(double share, double split)
    {
        ColTree.Width = new GridLength((1 - share) * split, GridUnitType.Star);
        ColEntries.Width = new GridLength((1 - share) * (1 - split), GridUnitType.Star);
        if (!_sensNarrow) ColSensPreview.Width = new GridLength(share, GridUnitType.Star);
    }

    private void SetSensNarrow(bool narrow)
    {
        _sensNarrow = narrow;
        ColSensGap.Width = new GridLength(narrow ? 0 : 20);
        SensPreviewSplitter.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        ColSensPreview.MinWidth = narrow ? 0 : 260;
        ColSensPreview.Width = narrow ? new GridLength(0) : new GridLength(_sensShare, GridUnitType.Star);
        SensPreviewToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        if (narrow)
        {
            // pannello sopra la lista "Nell'overlay", a destra, aperto solo a richiesta
            Grid.SetColumn(SensPreviewCard, 2);
            SensPreviewCard.HorizontalAlignment = HorizontalAlignment.Right;
            SensPreviewCard.Width = 280;
            SensPreviewCard.Margin = new Thickness(0, 64, 10, 10);
            Panel.SetZIndex(SensPreviewCard, 10);
            SensPreviewCard.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.45 };
            SensPreviewCard.Visibility = SensPreviewToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            Grid.SetColumn(SensPreviewCard, 4);
            SensPreviewCard.HorizontalAlignment = HorizontalAlignment.Stretch;
            SensPreviewCard.Width = double.NaN;
            SensPreviewCard.Margin = new Thickness(0);
            SensPreviewCard.Effect = null;
            SensPreviewCard.Visibility = Visibility.Visible;
        }
    }

    private void SensPreviewToggle_Click(object sender, RoutedEventArgs e) =>
        SensPreviewCard.Visibility = SensPreviewToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void StorePanels()
    {
        // Partita nell'area di notifica e mai aperta (o pagine mai viste): si tengono le misure salvate.
        double ovl = ColLook.ActualWidth + ColOvlPreview.ActualWidth;
        if (ColOvlPreview.ActualWidth > 0 && ovl > 0) _settings.OvlPreviewShare = ColOvlPreview.ActualWidth / ovl;
        double lists = ColTree.ActualWidth + ColEntries.ActualWidth;
        if (lists > 0) _settings.PanelSplit = ColTree.ActualWidth / lists;
        if (!_sensNarrow && ColSensPreview.ActualWidth > 0) _settings.SensPreviewShare = _sensShare = ColSensPreview.ActualWidth / (lists + ColSensPreview.ActualWidth);
        SaveSettingsSoon();
    }

    // Dopo ogni trascinamento le colonne tornano quote (*), così continuano a crescere con la finestra; e si salvano.
    private void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        StorePanels();
        double ovl = _settings.OvlPreviewShare is > 0.1 and < 0.8 ? _settings.OvlPreviewShare : DefaultOvlShare;
        ColLook.Width = new GridLength(1 - ovl, GridUnitType.Star);
        ColOvlPreview.Width = new GridLength(ovl, GridUnitType.Star);
        SetSensColumns(_sensShare, _settings.PanelSplit is > 0.05 and < 0.95 ? _settings.PanelSplit : DefaultTreeSplit);
    }

    // Doppio clic su una barra: si torna alle misure iniziali di quella pagina.
    private void Splitter_Reset(object sender, MouseButtonEventArgs e)
    {
        if (sender == OvlSplitter)
        {
            _settings.OvlPreviewShare = 0;
            ColLook.Width = new GridLength(1 - DefaultOvlShare, GridUnitType.Star);
            ColOvlPreview.Width = new GridLength(DefaultOvlShare, GridUnitType.Star);
        }
        else
        {
            _settings.SensPreviewShare = 0; _settings.PanelSplit = 0;
            _sensShare = DefaultSensShare;
            SetSensColumns(DefaultSensShare, DefaultTreeSplit);
        }
        SaveSettingsSoon();
    }

    // ------------------------------------------------------------ icona vicino all'orologio, chiusura, avvio

    private void ShowFromTray() => BringToFront();

    /// <summary>
    /// Riapre la finestra a grandezza piena e davanti alle altre: dall'icona vicino all'orologio,
    /// dal menu dell'icona e quando si avvia una seconda copia dell'app.
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        // WPF può credere la finestra normale mentre Windows la tiene ridotta a icona:
        // si chiede a Windows stesso di ripristinarla.
        if (_hwnd != IntPtr.Zero && Native.IsIconic(_hwnd)) Native.ShowWindow(_hwnd, Native.SW_RESTORE);
        KeepOnScreen();
        Activate();
        // Windows può rifiutare l'attivazione da un processo in secondo piano: il passaggio
        // da "sempre in primo piano" porta comunque la finestra davanti alle altre.
        bool top = Topmost;
        Topmost = true;
        Topmost = top;
        Focus();
    }

    // Se la finestra è finita fuori dagli schermi (uno schermo staccato, una risoluzione cambiata)
    // o ne resta visibile solo un pezzetto, la si rimette al centro dell'area di lavoro più vicina.
    private void KeepOnScreen()
    {
        if (_hwnd == IntPtr.Zero || WindowState != WindowState.Normal) return;
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        IntPtr monitor = Native.MonitorFromRect(ref r, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        var work = info.Work;
        int visibleW = Math.Min(r.Right, work.Right) - Math.Max(r.Left, work.Left);
        int visibleH = Math.Min(r.Bottom, work.Bottom) - Math.Max(r.Top, work.Top);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        // Basta che si veda una buona parte della finestra, barra del titolo compresa.
        bool titleVisible = r.Top >= work.Top - 8 && r.Top < work.Bottom - 40;
        if (visibleW >= Math.Min(w, 300) && visibleH >= Math.Min(h, 200) && titleVisible) return;
        int workW = work.Right - work.Left, workH = work.Bottom - work.Top;
        w = Math.Min(w, workW);
        h = Math.Min(h, workH);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, work.Left + (workW - w) / 2, work.Top + (workH - h) / 2, w, h,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        AppLog.Write($"Window moved back on screen (was {r.Left},{r.Top} {w}x{h})");
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void ShowTrayMenu()
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        var open = new MenuItem { Header = Loc.T("Tray.Open"), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => ShowFromTray();
        var overlay = new MenuItem { Header = Loc.T("Main.ShowOverlay"), IsCheckable = true, IsChecked = _settings.OverlayEnabled };
        overlay.Click += (_, _) => _settings.OverlayEnabled = overlay.IsChecked;
        var exit = new MenuItem { Header = Loc.T("Tray.Exit") };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(overlay);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        // Senza portare avanti la nostra finestra, il menu non si chiuderebbe cliccando altrove.
        Native.SetForegroundWindow(_hwnd);
        menu.IsOpen = true;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        StorePanels();
        if (!_exiting)
        {
            // La prima volta si chiede; poi vale la scelta fatta (si cambia dalle Opzioni).
            if (_settings.CloseToTray == null)
            {
                var dialog = new CloseDialog { Owner = this };
                dialog.ShowDialog();
                if (dialog.HideToTray == null) { e.Cancel = true; return; }
                _settings.CloseToTray = dialog.HideToTray;
            }
            if (_settings.CloseToTray == true)
            {
                e.Cancel = true;
                Hide();
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _tray?.Dispose();
        _pump.Dispose();
        _renderer.Dispose();
        SaveSettingsNow();
        foreach (int id in HotkeysPanel.Ids) Native.UnregisterHotKey(_hwnd, id);
        _overlay.Close();
        if (_ownsVulkanLayer) VulkanLayer.Unregister();
        _monitor.Dispose();
        _hook.Dispose();
        _hardware.Dispose();
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
