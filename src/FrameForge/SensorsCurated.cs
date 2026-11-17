using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// "Cosa mostrare": i valori che quasi tutti cercano, divisi per categoria e con nomi semplici.
/// Ogni voce corrisponde a un sensore vero del PC (trovato per componente, tipo e nome) oppure a una misura dei frame.
/// Le spunte sono le stesse dell'elenco completo: qui non c'è una seconda scelta da tenere allineata.
/// </summary>
public sealed class SensorsCurated : StackPanel
{
    private sealed record Item(string Key, string Cat, string? TipKey, Func<HardwareSnapshot, string?> Resolve, string? Frame = null);

    private static string? Find(HardwareSnapshot hw, string tag, string type, params string[] hints)
    {
        var pool = hw.All.Where(r => r.Tag == tag && r.Type == type
            && !r.GroupName.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (string hint in hints)
        {
            if (hint == "*") return pool.FirstOrDefault()?.Id;
            var hit = pool.FirstOrDefault(r => r.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit.Id;
        }
        return null;
    }

    private static string? Info(HardwareSnapshot hw, string id) => hw.ById.ContainsKey(SystemInfo.RootId + id) ? SystemInfo.RootId + id : null;

    private static Item Frame(string id, string? tip = "frame") => new("f." + id, "Perf", tip, _ => "frame:" + id, id);

    // l'ordine delle voci è quello in cui si leggono; le stesse regole della prima scelta automatica
    private static readonly Item[] Items =
    {
        Frame("fps", null), Frame("avg", null), Frame("low1"), Frame("low01"), Frame("ft"), Frame("p99"),
        Frame("smooth"), Frame("jitter"), Frame("stutter"), Frame("limit", null),
        new("gpu.load", "GPU", null, h => Find(h, "GPU", "Load", "GPU Core", "*")),
        new("gpu.temp", "GPU", null, h => Find(h, "GPU", "Temperature", "GPU Core", "*")),
        new("gpu.hot", "GPU", "Sns.Tip.hot", h => Find(h, "GPU", "Temperature", "Hot Spot", "Hotspot")),
        new("gpu.vram", "GPU", "Sns.Tip.vram", h => Find(h, "GPU", "SmallData", "GPU Memory Used", "Memory Used")),
        new("gpu.power", "GPU", null, h => Find(h, "GPU", "Power", "GPU Package", "*")),
        new("gpu.clock", "GPU", "Sns.Tip.clock", h => Find(h, "GPU", "Clock", "GPU Core", "*")),
        new("gpu.fan", "GPU", null, h => Find(h, "GPU", "Fan", "*")),
        new("cpu.load", "CPU", null, h => Find(h, "CPU", "Load", "CPU Total", "*")),
        new("cpu.temp", "CPU", null, h => Find(h, "CPU", "Temperature", "Tctl", "Package", "*")),
        new("cpu.power", "CPU", null, h => Find(h, "CPU", "Power", "Package")),
        new("cpu.clock", "CPU", "Sns.Tip.clock", h => Find(h, "CPU", "Clock", "Average", "Core #1")),
        new("ram.used", "RAM", null, h => Find(h, "RAM", "Data", "Memory Used")),
        new("ram.load", "RAM", null, h => Find(h, "RAM", "Load", "Memory")),
        new("ram.speed", "RAM", "Sns.Tip.speed", h => Info(h, "/ramspeed")),
        new("info.api", "Info", "Sns.Tip.api", h => Info(h, "/api")),
        new("info.session", "Info", null, h => Info(h, "/session")),
        new("info.driver", "Info", null, h => Info(h, "/driver")),
        new("info.vram", "Info", null, h => Info(h, "/vram")),
        new("info.ram", "Info", null, h => Info(h, "/ram")),
    };

    private static readonly (string Cat, int Glyph)[] Cats =
    {
        ("Perf", 0xE9D9), ("GPU", 0xE7F4), ("CPU", 0xE950), ("RAM", 0xE964), ("Info", 0xE946),
    };

    /// <summary>Le partenze rapide: cambiano solo le voci di questo elenco, mai le altre scelte nell'elenco completo.</summary>
    private static readonly (string Key, string[] On)[] Presets =
    {
        ("Sns.Essential", new[] { "f.fps", "f.low1", "gpu.load", "gpu.temp", "cpu.load", "cpu.temp" }),
        ("Sns.Gaming", new[] { "f.fps", "f.avg", "f.low1", "f.ft", "gpu.load", "gpu.temp", "gpu.vram", "gpu.power", "cpu.load", "cpu.temp", "ram.used" }),
        ("Sns.Full", Array.Empty<string>()),   // tutto quello che c'è su questo PC
    };

    private readonly Settings _s;
    private readonly Action _changed;
    private readonly Dictionary<string, string> _ids = new();          // voce → sensore trovato (o "frame:...")
    private readonly Dictionary<string, CheckBox> _checks = new();
    private readonly Dictionary<string, TextBlock> _values = new();
    private readonly List<(FrameworkElement Row, string Text)> _rows = new();
    private readonly List<(FrameworkElement Head, List<FrameworkElement> Rows)> _cats = new();
    private readonly List<(CheckBox Chip, (string Key, string[] On) Preset)> _chips = new();
    private TextBlock? _noMatch;
    private int _version = -1;
    private bool _syncing;
    private string _query = "";

    public SensorsCurated(Settings s, Action changed)
    {
        _s = s;
        _changed = changed;
    }

    /// <summary>I pulsanti delle partenze rapide (stanno sopra la ricerca).</summary>
    public void BuildPresets(Panel host)
    {
        host.Children.Clear();
        _chips.Clear();
        foreach (var p in Presets)
        {
            var chip = new CheckBox { Content = Loc.T(p.Key), Margin = new Thickness(0, 0, 8, 6) };
            chip.SetResourceReference(StyleProperty, "Chip");
            chip.Click += (_, _) => ApplyPreset(p.On);
            _chips.Add((chip, p));
            host.Children.Add(chip);
        }
        SyncChecks();
    }

    /// <summary>Si rifà solo quando cambia l'insieme dei sensori (o la lingua: version -1); altrimenti aggiorna valori e spunte.</summary>
    public void Sync(HardwareSnapshot hw, bool force = false)
    {
        if (force || hw.Version != _version) Rebuild(hw);
        foreach (var (key, tb) in _values)
            if (_ids.TryGetValue(key, out var id) && hw.ById.TryGetValue(id, out var r))
            {
                var (v, unit) = SensorFormat.Format(r);
                tb.Text = unit.Length == 0 ? v : v + " " + unit;
            }
        SyncChecks();
    }

    private void Rebuild(HardwareSnapshot hw)
    {
        _version = hw.Version;
        Children.Clear();
        _ids.Clear(); _checks.Clear(); _values.Clear(); _rows.Clear(); _cats.Clear();
        foreach (var it in Items)
            if (it.Resolve(hw) is { } id) _ids[it.Key] = id;

        foreach (var (cat, glyph) in Cats)
        {
            var head = new DockPanel { Margin = new Thickness(0, Children.Count == 0 ? 2 : 16, 0, 6) };
            var icon = Icon(glyph, 13, "AccentText");
            icon.Margin = new Thickness(0, 0, 9, 0);
            head.Children.Add(icon);
            head.Children.Add(Text(Loc.T("Sns.Cat." + cat), 13, "Fg", true));
            Children.Add(head);
            var rows = new List<FrameworkElement>();
            foreach (var it in Items.Where(i => i.Cat == cat))
            {
                var row = Row(it);
                rows.Add(row);
                Children.Add(row);
            }
            _cats.Add((head, rows));
        }
        _noMatch = Text(Loc.T("Sns.NoMatch"), 12.5, "FgDim");
        _noMatch.TextWrapping = TextWrapping.Wrap;
        _noMatch.Margin = new Thickness(0, 4, 0, 0);
        Children.Add(_noMatch);
        Filter(_query);
    }

    private FrameworkElement Row(Item it)
    {
        bool has = _ids.ContainsKey(it.Key);
        string name = Loc.T("Sns." + it.Key);
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(Text(name, 13, "Fg"));
        string? tip = it.TipKey == "frame" ? OverlayModel.FrameDescription(it.Frame!) : it.TipKey != null ? Loc.T(it.TipKey) : null;
        if (tip != null) label.Children.Add(InfoTip(name, tip));

        var cb = new CheckBox { Content = label, IsEnabled = has, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(cb, name);
        cb.Click += (_, _) => Toggle(it, cb.IsChecked == true);
        var row = new DockPanel { Margin = new Thickness(22, 0, 0, 0), MinHeight = 30 };
        var value = Text(has ? "" : Loc.T("Sns.NA"), 12, "FgDim");
        value.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(cb);
        if (has)
        {
            _checks[it.Key] = cb;
            if (it.Frame == null) _values[it.Key] = value;
        }
        else row.ToolTip = Loc.T("Sns.NA");
        _rows.Add((row, name + " " + Loc.T("Sns.Cat." + it.Cat) + " " + (tip ?? "")));
        return row;
    }

    private bool IsOn(string key)
    {
        if (!_ids.TryGetValue(key, out var id)) return false;
        if (id.StartsWith("frame:"))
            return OverlayModel.AllFrameItems(_s).FirstOrDefault(f => "frame:" + f.Id == id) is { } fi && fi.Hidden != true;
        return _s.HasSensor(id);
    }

    private void Set(string key, bool on)
    {
        if (!_ids.TryGetValue(key, out var id)) return;
        if (id.StartsWith("frame:"))
        {
            if (OverlayModel.AllFrameItems(_s).FirstOrDefault(f => "frame:" + f.Id == id) is { } fi) fi.Hidden = on ? null : true;
        }
        else if (on != _s.HasSensor(id))
        {
            if (on) _s.Sensors.Add(new SensorItem { Id = id });
            else _s.Sensors.RemoveAll(x => x.Id == id);
        }
    }

    private void Toggle(Item it, bool on)
    {
        if (_syncing) return;
        Set(it.Key, on);
        _changed();
        SyncChecks();
    }

    /// <summary>Partenza rapida per numero (0 = Essenziale, 1 = Da gioco, 2 = Completo): la usa anche la guida iniziale.</summary>
    public void ApplyPreset(int index) => ApplyPreset(Presets[index].On);

    /// <summary>La partenza rapida che corrisponde alle spunte di adesso, oppure -1.</summary>
    public int CurrentPreset()
    {
        var current = _ids.Keys.Where(IsOn).ToHashSet();
        for (int i = 0; i < Presets.Length; i++)
            if (current.SetEquals((Presets[i].On.Length == 0 ? _ids.Keys : Presets[i].On.Where(_ids.ContainsKey)).ToHashSet())) return i;
        return -1;
    }

    private void ApplyPreset(string[] on)
    {
        foreach (var it in Items)
            Set(it.Key, on.Length == 0 || on.Contains(it.Key));
        _changed();
        SyncChecks();
    }

    /// <summary>Le spunte seguono le impostazioni (anche quando si cambia qualcosa dall'elenco completo o dalla lista a destra).</summary>
    public void SyncChecks()
    {
        _syncing = true;
        foreach (var (key, cb) in _checks)
        {
            bool on = IsOn(key);
            if (cb.IsChecked != on) cb.IsChecked = on;
        }
        // la partenza rapida accesa è quella che corrisponde esattamente alle spunte di adesso
        var current = _ids.Keys.Where(IsOn).ToHashSet();
        foreach (var (chip, p) in _chips)
        {
            var want = (p.On.Length == 0 ? _ids.Keys : p.On.Where(_ids.ContainsKey)).ToHashSet();
            chip.IsChecked = current.SetEquals(want);
        }
        _syncing = false;
    }

    /// <summary>Ricerca: lascia le righe che corrispondono; le categorie vuote spariscono.</summary>
    public bool Filter(string q)
    {
        _query = q;
        bool any = false;
        var vis = new HashSet<FrameworkElement>();
        foreach (var (row, text) in _rows)
        {
            bool m = q.Length == 0 || text.Contains(q, StringComparison.OrdinalIgnoreCase);
            row.Visibility = m ? Visibility.Visible : Visibility.Collapsed;
            if (m) vis.Add(row);
        }
        foreach (var (head, rows) in _cats)
        {
            bool show = rows.Any(vis.Contains);
            head.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            any |= show;
        }
        if (_noMatch != null) _noMatch.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        return any;
    }

    // ------------------------------------------------------------ mattoncini

    private static TextBlock Text(string text, double size, string brush, bool bold = false)
    {
        var t = new TextBlock { Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (bold) t.FontWeight = FontWeights.SemiBold;
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    private static TextBlock Icon(int glyph, double size, string brush)
    {
        var t = Text(char.ConvertFromUtf32(glyph), size, brush);
        t.FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        return t;
    }

    /// <summary>Il simbolo ⓘ con la spiegazione dei nomi tecnici.</summary>
    private static TextBlock InfoTip(string title, string text)
    {
        var i = Icon(0xE946, 11, "FgDim");
        i.Margin = new Thickness(6, 1, 0, 0);
        i.Cursor = Cursors.Help;
        var tip = new StackPanel { MaxWidth = 300 };
        tip.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) });
        tip.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        i.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(i, 150);
        return i;
    }
}
