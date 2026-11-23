using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameGrip.Core;

/// <summary>Un sensore mostrato nell'overlay, con nome e colore scelti dall'utente (null = predefiniti).</summary>
public sealed class SensorItem
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Color { get; set; }
    /// <summary>Grandezza del testo di questo sensore rispetto alla sua riga: 1 = normale.</summary>
    public double? Size { get; set; }
    /// <summary>Solo per le misure dei frame: vero = spenta (resta in elenco, ma non compare nell'overlay).</summary>
    public bool? Hidden { get; set; }
}

/// <summary>Etichetta, colore e grandezza personalizzati di una riga dell'overlay (GPU, CPU, FPS...).</summary>
/// <summary>Il limite di FPS di un gioco (chiave: nome dell'eseguibile in minuscolo, es. "cs2.exe").</summary>
public sealed class GameProfile
{
    public int Fps { get; set; } = 60;
    public string DisplayName { get; set; } = "";
    public DateTime LastSeen { get; set; }
    // dal negozio (se il gioco è nella libreria): servono a riconoscerlo anche se parte da un altro eseguibile
    public string? Store { get; set; }
    public string? AppId { get; set; }
    public string? Folder { get; set; }
}

/// <summary>Un gioco agganciato di recente: serve a "Aggiungi un gioco…" nei profili.</summary>
public sealed class GameSeen
{
    public string Exe { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastSeen { get; set; }
    public string? Store { get; set; }
}

public sealed class GroupStyle
{
    public string? Label { get; set; }
    public string? Color { get; set; }
    /// <summary>Grandezza del testo della riga rispetto al resto: 1 = normale, 1.5 = una volta e mezza.</summary>
    public double? Size { get; set; }
}

/// <summary>Le grandezze del testo di uno stile dell'overlay: il cursore "Grandezza testo" e le grandezze A−/A+
/// di righe e sensori (chiave "g:" riga, "s:" sensore, "f:" misura dei frame; 0 = normale).</summary>
public sealed class LayoutSizes
{
    public double Scale { get; set; } = 1.0;
    public Dictionary<string, double> Items { get; set; } = new();
}

public sealed class Settings : INotifyPropertyChanged, IJsonOnDeserializing, IJsonOnDeserialized
{
    private bool _overlayEnabled = true;
    private bool _showFps = true, _showLows = true, _showFrametime = true, _showPacing = true, _showGraph = true, _showApi = true;
    private int _layout;            // 0 classico, 1 lista, 2 barra
    private int _theme;             // indice in Theme.All
    private int _font;              // 0 mono, 1 pulito, 2 tecnico
    private int _corner;            // 0 alto-sx, 1 alto-dx, 2 basso-sx, 3 basso-dx
    private double _scale = 1.0;
    private double _opacity = 0.6;
    private bool _limiterEnabled;
    private int _limiterFps = 60;
    private uint _hotkeyModifiers = 0x2 | 0x4;  // Ctrl + Shift
    private int _hotkeyVk = 0x4F;               // O
    private string _hotkeyName = "Ctrl + Shift + O";

    public bool OverlayEnabled { get => _overlayEnabled; set => Set(ref _overlayEnabled, value); }
    public bool ShowFps { get => _showFps; set => Set(ref _showFps, value); }
    public bool ShowLows { get => _showLows; set => Set(ref _showLows, value); }
    public bool ShowFrametime { get => _showFrametime; set => Set(ref _showFrametime, value); }
    public bool ShowPacing { get => _showPacing; set => Set(ref _showPacing, value); }
    public bool ShowGraph { get => _showGraph; set => Set(ref _showGraph, value); }
    public bool ShowApi { get => _showApi; set => Set(ref _showApi, value); }
    private string _appAccent = DefaultAccent;
    public const string DefaultAccent = "#4C9BFF";
    /// <summary>Colore d'accento della finestra dell'app (non dell'overlay), in formato #RRGGBB.</summary>
    public string AppAccent { get => _appAccent; set => Set(ref _appAccent, string.IsNullOrWhiteSpace(value) ? DefaultAccent : value); }
    private string? _language;
    /// <summary>Lingua dell'interfaccia: "en" o "it". Se manca nel file vale quella di Windows (italiano o inglese).</summary>
    public string Language
    {
        get => _language ??= Loc.SystemDefault();
        set => Set(ref _language, Loc.Normalize(value));
    }

    private string _appTheme = Theming.Dark;
    /// <summary>Tema della finestra dell'app: "dark" o "light". Da non confondere con <see cref="Theme"/>, il tema dell'overlay.</summary>
    public string AppTheme { get => _appTheme; set => Set(ref _appTheme, Theming.Normalize(value)); }

    /// <summary>Numero degli stili dell'overlay (vedi OverlayView.StyleList e seguenti).</summary>
    public const int LayoutCount = 7;
    /// <summary>0 lista, 1 barra, 2 Grid HUD, 3 Console, 4 Dashboard, 5 Anelli, 6 Icone.</summary>
    public int Layout
    {
        get => _layout;
        set
        {
            // Durante la lettura del file si tiene il numero com'è: la conversione delle versioni vecchie
            // in Load() deve vedere il valore salvato (prima di Cards gli stili erano sette).
            if (_reading) { Set(ref _layout, Math.Max(value, 0)); return; }
            int v = Math.Clamp(value, 0, LayoutCount - 1);
            if (v == _layout) return;
            SwapSizes(_layout, v);
            Set(ref _layout, v);
        }
    }

    /// <summary>
    /// Le grandezze del testo degli altri stili. Quelle dello stile scelto stanno nei posti di sempre (Scale e
    /// Size di righe e sensori): cambiando stile si mettono da parte e si riprendono quelle del nuovo stile.
    /// Uno stile mai usato parte dalle grandezze attuali, così per chi aggiorna non cambia nulla.
    /// </summary>
    public Dictionary<int, LayoutSizes> SizesByLayout { get; set; } = new();

    private const int StyleListId = 0;
    private bool _reading;   // durante la lettura del file i setter non devono scambiare nulla
    void IJsonOnDeserializing.OnDeserializing() => _reading = true;
    void IJsonOnDeserialized.OnDeserialized() => _reading = false;

    private void SwapSizes(int from, int to)
    {
        var keep = new LayoutSizes { Scale = _scale };
        foreach (var s in Sensors) keep.Items["s:" + s.Id] = s.Size ?? 0;
        foreach (var f in FrameItems) keep.Items["f:" + f.Id] = f.Size ?? 0;
        foreach (var (key, g) in Groups) keep.Items["g:" + key] = g.Size ?? 0;
        SizesByLayout[from] = keep;
        if (!SizesByLayout.TryGetValue(to, out var next)) return;   // stile nuovo: tiene le grandezze attuali
        static double? Of(double v) => v <= 0 || Math.Abs(v - 1) < 0.001 ? null : v;
        foreach (var s in Sensors) if (next.Items.TryGetValue("s:" + s.Id, out var v)) s.Size = Of(v);
        foreach (var f in FrameItems) if (next.Items.TryGetValue("f:" + f.Id, out var v)) f.Size = Of(v);
        foreach (var (key, g) in Groups) if (next.Items.TryGetValue("g:" + key, out var v)) g.Size = Of(v);
        Scale = next.Scale;
        NotifySensors();   // l'overlay e l'elenco "Personalizza" rileggono le grandezze
    }
    /// <summary>Serve a convertire una sola volta le impostazioni salvate quando gli stili erano tre.</summary>
    public int LayoutVersion { get; set; }
    public int Theme { get => _theme; set => Set(ref _theme, Math.Max(value, 0)); }
    public int Font { get => _font; set => Set(ref _font, Math.Clamp(value, 0, 2)); }
    public int Corner
    {
        get => _corner;   // 4 = in alto al centro
        set { Set(ref _corner, Math.Clamp(value, 0, 4)); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CornerIndex))); }
    }
    /// <summary>La stessa posizione nell'ordine dei pulsanti dell'interfaccia: ↖ ↑ ↗ ↙ ↘.</summary>
    private static readonly int[] CornerOrder = { 0, 4, 1, 2, 3 };
    [System.Text.Json.Serialization.JsonIgnore]
    public int CornerIndex
    {
        get => Array.IndexOf(CornerOrder, _corner);
        set { if (value >= 0 && value < CornerOrder.Length) Corner = CornerOrder[value]; }
    }
    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.5, 2.5)); }
    public double Opacity { get => _opacity; set => Set(ref _opacity, Math.Clamp(value, 0, 1)); }
    private double _hdrWhiteNits = 200;
    /// <summary>Luminosità dell'overlay nei giochi HDR: il bianco dell'overlay, in nit (80-400).</summary>
    public double HdrWhiteNits { get => _hdrWhiteNits; set => Set(ref _hdrWhiteNits, Math.Clamp(Math.Round(value / 10) * 10, 80, 400)); }

    private bool? _closeToTray;
    /// <summary>Cosa fa la X della finestra: true nasconde l'app vicino all'orologio, false la chiude,
    /// null = non ancora scelto (la prima volta lo si chiede).</summary>
    public bool? CloseToTray
    {
        get => _closeToTray;
        set { if (_closeToTray == value) return; Set(ref _closeToTray, value); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideOnClose))); }
    }
    /// <summary>La stessa scelta, per l'interruttore dell'interfaccia.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HideOnClose { get => _closeToTray == true; set => CloseToTray = value; }

    public bool LimiterEnabled { get => _limiterEnabled; set => Set(ref _limiterEnabled, value); }
    public int LimiterFps { get => _limiterFps; set => Set(ref _limiterFps, Math.Clamp(value, 20, 500)); }

    /// <summary>Limiti per gioco: valgono al posto di LimiterFps quando quel gioco è agganciato.</summary>
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Gli ultimi giochi agganciati (al più 30), per aggiungere un profilo senza scrivere il nome.</summary>
    public List<GameSeen> GameHistory { get; set; } = new();
    private bool? _vrrDisplay;
    /// <summary>Lo schermo ha G-Sync/FreeSync (VRR)? Lo dice l'utente: Windows non lo rivela in modo affidabile. null = non detto.</summary>
    public bool? VrrDisplay { get => _vrrDisplay; set => Set(ref _vrrDisplay, value); }
    private int _idealMinutes = 2;
    /// <summary>Durata della misura del limite ideale, in minuti (2, 5 o 10; 3 resta per le impostazioni vecchie).</summary>
    public int IdealMinutes { get => _idealMinutes; set => Set(ref _idealMinutes, value is 3 or 5 or 10 ? value : 2); }
    /// <summary>Generazione di frame scelta l'ultima volta per ogni gioco (eseguibile → 1..4): la usa anche il tasto in gioco.</summary>
    public Dictionary<string, int> IdealFg { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Durata della misura scelta per ogni gioco (eseguibile → minuti). Senza scelta vale IdealMinutes.</summary>
    public Dictionary<string, int> IdealMinutesByGame { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Valore salvato per un gioco, cercato con tutti i suoi eseguibili. Ogni gioco ha i suoi valori:
    /// la pagina di un gioco non scrive mai nei campi comuni.
    /// </summary>
    public static int PerGame(Dictionary<string, int> map, IEnumerable<string> keys, int fallback)
    {
        foreach (var k in keys)
            if (map.TryGetValue(k, out int v)) return v;
        return fallback;
    }

    public void SetPerGame(Dictionary<string, int> map, IEnumerable<string> keys, int value)
    {
        foreach (var k in keys) map[k] = value;
        NotifyProfiles();   // le raccolte non avvisano da sole: così le impostazioni si salvano
    }
    private bool _idealSound = true;
    /// <summary>Suono di Windows quando la misura parte e quando finisce.</summary>
    public bool IdealSound { get => _idealSound; set => Set(ref _idealSound, value); }
    private bool _profilesOpen = true;
    public bool ProfilesOpen { get => _profilesOpen; set => Set(ref _profilesOpen, value); }

    /// <summary>Da chiamare dopo aver cambiato profili o cronologia dei giochi (sono raccolte: Set non le vede).</summary>
    public void NotifyProfiles() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GameProfiles)));

    /// <summary>Tasto che mostra/nasconde l'overlay: modificatori Win32 (Alt 1, Ctrl 2, Shift 4, Win 8) e codice tasto.</summary>
    public uint HotkeyModifiers { get => _hotkeyModifiers; set => Set(ref _hotkeyModifiers, value); }
    public int HotkeyVk { get => _hotkeyVk; set => Set(ref _hotkeyVk, value); }
    public string HotkeyName { get => _hotkeyName; set => Set(ref _hotkeyName, value); }

    private uint _recordModifiers = 0x2 | 0x4;  // Ctrl + Shift
    private int _recordVk = 0x42;               // B
    private string _recordName = "Ctrl + Shift + B";

    /// <summary>Tasto che avvia/ferma la registrazione del benchmark.</summary>
    public uint RecordModifiers { get => _recordModifiers; set => Set(ref _recordModifiers, value); }
    public int RecordVk { get => _recordVk; set => Set(ref _recordVk, value); }
    public string RecordName { get => _recordName; set => Set(ref _recordName, value); }

    private uint _idealModifiers = 0x2 | 0x4;   // Ctrl + Shift
    private int _idealVk = 0x4D;                // M
    private string _idealName = "Ctrl + Shift + M";

    /// <summary>Tasto che avvia/ferma la misura del limite ideale direttamente in gioco. Vk 0 = nessun tasto.</summary>
    public uint IdealModifiers { get => _idealModifiers; set => Set(ref _idealModifiers, value); }
    public int IdealVk { get => _idealVk; set => Set(ref _idealVk, value); }
    public string IdealName { get => _idealName; set => Set(ref _idealName, value); }

    /// <summary>I tasti di fabbrica: 1 = overlay, 2 = registrazione, 3 = misura del limite ideale.</summary>
    public static (uint Mods, int Vk, string Name) DefaultKey(int id) => id switch
    {
        1 => (0x2 | 0x4, 0x4F, "Ctrl + Shift + O"),
        2 => (0x2 | 0x4, 0x42, "Ctrl + Shift + B"),
        _ => (0x2 | 0x4, 0x4D, "Ctrl + Shift + M"),
    };

    public (uint Mods, int Vk, string Name) Key(int id) => id switch
    {
        1 => (HotkeyModifiers, HotkeyVk, HotkeyName),
        2 => (RecordModifiers, RecordVk, RecordName),
        _ => (IdealModifiers, IdealVk, IdealName),
    };

    public void SetKey(int id, uint mods, int vk, string name)
    {
        switch (id)
        {
            case 1: HotkeyModifiers = mods; HotkeyVk = vk; HotkeyName = name; break;
            case 2: RecordModifiers = mods; RecordVk = vk; RecordName = name; break;
            default: IdealModifiers = mods; IdealVk = vk; IdealName = name; break;
        }
    }

    /// <summary>Nomi di programmi (senza .exe) da agganciare sempre, anche se non riconosciuti come giochi.</summary>
    public List<string> Games { get; set; } = new();

    /// <summary>Sensori mostrati nell'overlay, nell'ordine scelto dall'utente.</summary>
    public List<SensorItem> Sensors { get; set; } = new();

    /// <summary>Misure dei frame (FPS, 1% low, frametime, jitter...) mostrate sotto la riga FPS, nell'ordine
    /// scelto dall'utente, con nome, colore e grandezza personalizzabili come per i sensori.</summary>
    public List<SensorItem> FrameItems { get; set; } = new();

    /// <summary>
    /// I componenti aperti nell'elenco "Scegli i sensori", per identificativo dell'hardware (es. "/gpu-nvidia/0"),
    /// non per posizione. Tutto il resto parte chiuso, anche i componenti nuovi. Sta nel file delle impostazioni,
    /// che l'installazione non tocca: la scelta resta tra un avvio e l'altro e dopo un aggiornamento.
    /// </summary>
    public List<string> TreeExpanded { get; set; } = new();

    /// <summary>Stile delle righe: la chiave è l'identificativo dell'hardware oppure "frame:FPS" e simili.</summary>
    public Dictionary<string, GroupStyle> Groups { get; set; } = new();

    /// <summary>Pagina Sensori: quota di larghezza dell'elenco dei sensori rispetto a "Nell'overlay" (0 = misura iniziale).</summary>
    public double PanelSplit { get; set; }
    /// <summary>Quota della larghezza della pagina presa dall'anteprima, pagina Overlay e pagina Sensori (0 = misura iniziale).</summary>
    public double OvlPreviewShare { get; set; }
    public double SensPreviewShare { get; set; }

    /// <summary>Vero dopo che la selezione iniziale dei sensori è stata proposta una volta.</summary>
    public bool DefaultsApplied { get; set; }
    /// <summary>Guida iniziale già vista. Vero per chi aveva già le impostazioni: la vede solo chi parte da zero.</summary>
    public bool GuideDone { get; set; } = true;

    /// <summary>Formato delle versioni precedenti (solo elenco di identificativi): convertito al caricamento.</summary>
    [JsonPropertyName("SensorIds")]
    public List<string>? LegacySensorIds { get; set; }

    [JsonIgnore]
    public IEnumerable<string> SelectedIds => Sensors.Select(s => s.Id);

    public bool HasSensor(string id) => Sensors.Any(s => s.Id == id);

    public void SetSensor(string id, bool on)
    {
        var existing = Sensors.FirstOrDefault(s => s.Id == id);
        if (on == (existing != null)) return;
        if (on) Sensors.Add(new SensorItem { Id = id }); else Sensors.Remove(existing!);
        NotifySensors();
    }

    public GroupStyle? StyleOf(string key) => Groups.TryGetValue(key, out var g) ? g : null;

    public GroupStyle StyleFor(string key)
    {
        if (!Groups.TryGetValue(key, out var g)) Groups[key] = g = new GroupStyle();
        return g;
    }

    /// <summary>Da chiamare dopo aver modificato sensori, nomi, colori o ordine.</summary>
    public void NotifySensors() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sensors)));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>La variabile d'ambiente FRAMEGRIP_SETTINGS permette di usare un file diverso (prove, profili).</summary>
    private static string FilePath => Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameGrip", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Settings Load()
    {
        Settings s;
        bool fresh = false;
        try
        {
            // L'app si chiamava "FrameForge": al primo avvio col nuovo nome si riprendono le impostazioni di prima.
            string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameForge", "settings.json");
            // (non con un file scelto da FRAMEGRIP_SETTINGS: le prove partono davvero da zero)
            if (Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is not { Length: > 0 } && !File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.Copy(old, FilePath);
            }
            fresh = !File.Exists(FilePath) && !File.Exists(FilePath + ".bak");
            // File rovinato: se ne tiene una copia, si prova il .bak, altrimenti si riparte dai valori predefiniti.
            s = SafeFile.Read(FilePath, json => JsonSerializer.Deserialize<Settings>(json), "Settings") ?? new Settings();
        }
        catch (Exception ex)
        {
            AppLog.Write("Settings: " + ex.Message);
            s = new Settings();
        }

        if (fresh) s.GuideDone = false;   // prima apertura in assoluto: si mostra la guida

        if (s.LegacySensorIds is { Count: > 0 } && s.Sensors.Count == 0)
            s.Sensors = s.LegacySensorIds.Select(id => new SensorItem { Id = id }).ToList();
        s.LegacySensorIds = null;

        // Lo stile "classico" è stato rimosso: gli stili ora sono 0 = lista, 1 = barra.
        if (s.LayoutVersion < 2)
        {
            s._layout = s._layout == 2 ? 1 : 0;
            s.LayoutVersion = 2;
        }
        // Versione 3: arrivano cinque stili nuovi (2-6) dopo Lista e Barra; i numeri esistenti non cambiano.
        if (s.LayoutVersion < 3) s.LayoutVersion = 3;
        // Versione 4: grandezze del testo per stile (SizesByLayout). Le grandezze salvate restano quelle dello stile
        // in uso; gli altri stili le prendono da lì la prima volta che si scelgono.
        if (s.LayoutVersion < 4) s.LayoutVersion = 4;
        // Versione 5: tolto lo stile Cards (4); Telemetry e Console scalano a 4 e 5. Chi usava Cards passa a
        // Grid HUD. Inoltre il 100% ora vale la stessa grandezza in tutti gli stili (quella della Lista): una
        // volta sola ogni stile riparte dalla grandezza del testo della Lista, così nessuno stile resta enorme
        // o minuscolo per via delle vecchie misure diverse.
        if (s.LayoutVersion < 5)
        {
            static int Map(int v) => v switch { 4 => 2, 5 => 4, 6 => 5, _ => v };
            double listScale = s._layout == StyleListId ? s._scale
                : s.SizesByLayout.TryGetValue(StyleListId, out var ls) ? ls.Scale : s._scale;
            var moved = new Dictionary<int, LayoutSizes>();
            foreach (var (k, v) in s.SizesByLayout)
            {
                if (k == 4 || k < 0 || k > 6) continue;   // le grandezze di Cards non servono più
                moved[Map(k)] = v;
            }
            foreach (var (k, v) in moved) if (k != StyleListId) v.Scale = listScale;
            s.SizesByLayout = moved;
            s._layout = Map(s._layout);
            s.SizesByLayout.Remove(s._layout);   // quelle dello stile in uso stanno già in Scale e nelle Size
            if (s._layout != StyleListId) s.Scale = listScale;
            s.LayoutVersion = 5;
        }
        // Versione 6: tolti Ribbon (3) e Telemetry (4). Chi usava Ribbon passa a Barra, chi usava Telemetry al
        // Dashboard (stesso numero 4); Console scala da 5 a 3; arrivano Dashboard (4), Anelli (5) e Icone (6).
        if (s.LayoutVersion < 6)
        {
            static int Map6(int v) => v switch { 3 => 1, 5 => 3, _ => v };
            var moved = new Dictionary<int, LayoutSizes>();
            foreach (var (k, v) in s.SizesByLayout.OrderBy(kv => kv.Key == 3 ? 1 : 0))   // la Barra vera vince su quella venuta da Ribbon
            {
                if (k < 0 || k > 5) continue;
                moved.TryAdd(Map6(k), v);
            }
            s.SizesByLayout = moved;
            s._layout = Map6(s._layout);
            s.SizesByLayout.Remove(s._layout);
            s.LayoutVersion = 6;
        }
        s._layout = Math.Clamp(s._layout, 0, LayoutCount - 1);
        return s;
    }

    public void Save() => SaveJson(ToJson());

    /// <summary>Le impostazioni come testo JSON (quello del file).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Una copia indipendente, da dare a un altro thread (es. quello che disegna l'overlay).</summary>
    public static Settings FromJson(string json) => JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();

    /// <summary>
    /// Scrive su disco un testo già preparato: si può chiamare da un thread qualunque. Con più salvataggi
    /// in corsa vince quello con il numero d'ordine più alto (mai un testo vecchio sopra uno nuovo).
    /// </summary>
    public static void SaveJson(string json, long order = long.MaxValue)
    {
        lock (SaveGate)
        {
            if (order < _lastOrder) return;
            _lastOrder = order == long.MaxValue ? _lastOrder : order;
            // .tmp, scrittura forzata su disco e scambio: un'interruzione (crash, corrente) non lascia mai un file a metà
            try { SafeFile.WriteAllText(FilePath, json); }
            catch (Exception ex) { AppLog.Write("Settings save: " + ex.Message); }   // il salvataggio non deve mai far cadere l'app
        }
    }

    private static readonly object SaveGate = new();
    private static long _lastOrder;
}
