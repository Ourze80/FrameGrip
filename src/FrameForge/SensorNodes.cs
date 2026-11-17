using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FrameGrip.Core;

namespace FrameGrip;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ------------------------------------------------------------ elenco di tutti i sensori

public abstract class NodeBase : Observable
{
    private bool _isExpanded;
    private Visibility _vis = Visibility.Visible;

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public Visibility Vis { get => _vis; set => Set(ref _vis, value); }
}

public sealed class HardwareNode : NodeBase
{
    public string GroupId { get; }
    public string Tag { get; }
    public string Name { get; }
    public ObservableCollection<SensorNode> Children { get; } = new();

    public HardwareNode(string groupId, string tag, string name)
    {
        GroupId = groupId;
        Tag = tag;
        Name = name;
    }
}

public sealed class SensorNode : NodeBase
{
    private readonly Action<SensorNode> _onToggle;
    private bool _isSelected;
    private string _valueText = "";

    public string Id { get; }
    public string Type { get; }
    public string Name { get; }
    public string TypeName { get; }

    public SensorNode(string id, string type, string name, string typeName, bool selected, Action<SensorNode> onToggle)
    {
        Id = id;
        Type = type;
        Name = name;
        TypeName = typeName;
        _isSelected = selected;
        _onToggle = onToggle;
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _onToggle(this); }
    }

    /// <summary>Cambia la spunta senza avvisare (la modifica arriva già dalle impostazioni).</summary>
    public void SetSelectedSilently(bool value)
    {
        if (_isSelected == value) return;
        _isSelected = value;
        Raise(nameof(IsSelected));
    }

    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
}

// ------------------------------------------------------------ voci personalizzabili dell'overlay

/// <summary>Una voce dell'overlay di cui si possono cambiare testo e colore.</summary>
public abstract class EntryBase : Observable
{
    private readonly string _defaultText;
    private readonly Brush _defaultBrush;
    protected readonly Action Changed;

    protected EntryBase(string defaultText, Brush defaultBrush, Action changed)
    {
        _defaultText = defaultText;
        _defaultBrush = defaultBrush;
        Changed = changed;
    }

    private string? _typed;   // testo come è stato battuto nella casella, spazi compresi

    /// <summary>Si è usciti dalla casella del nome: torna a mostrare il nome salvato (o quello predefinito).</summary>
    public void EndNameEdit()
    {
        if (_typed == null) return;
        _typed = null;
        Raise(nameof(Text));
    }
    protected abstract string? StoredText { get; set; }
    protected abstract string? StoredColor { get; set; }

    public string Text
    {
        get
        {
            string text = string.IsNullOrWhiteSpace(StoredText) ? _defaultText : StoredText!;
            // Mentre si scrive, la casella deve tenere gli spazi appena battuti (per esempio dopo una parola):
            // si salva il nome ripulito, ma si mostra quello scritto finché corrisponde.
            // Vale anche per la casella svuotata del tutto: resta vuota finché non si scrive il nome nuovo
            // (il nome predefinito torna solo quando si esce dalla casella lasciandola vuota).
            return _typed != null && (_typed.Trim() == text || _typed.Trim().Length == 0) ? _typed : text;
        }
        set
        {
            _typed = value;
            string? custom = string.IsNullOrWhiteSpace(value) || value.Trim() == _defaultText ? null : value.Trim();
            if (custom == StoredText) return;
            StoredText = custom;
            Raise();
            Changed();
        }
    }

    /// <summary>Colore in formato "#RRGGBB"; null = quello del tema.</summary>
    public string? Color
    {
        get => StoredColor;
        set
        {
            if (value == StoredColor) return;
            StoredColor = value;
            Raise();
            Raise(nameof(ColorBrush));
            Changed();
        }
    }

    public Brush ColorBrush => OverlayView.ParseBrush(StoredColor) ?? _defaultBrush;

    protected abstract double? StoredSize { get; set; }

    /// <summary>Grandezza del testo: 1 = normale. Per una riga vale per tutta la riga, per un sensore solo per lui.</summary>
    public double Size
    {
        get => StoredSize ?? 1.0;
        set
        {
            double v = Math.Round(Math.Clamp(value, 0.6, 2.5), 1);
            if (Math.Abs(v - Size) < 0.001) return;
            StoredSize = Math.Abs(v - 1.0) < 0.001 ? null : v;
            Raise();
            Raise(nameof(SizeText));
            Changed();
        }
    }

    public string SizeText => $"{Size * 100:0}%";

    /// <summary>Da chiamare quando la grandezza cambia da fuori (per esempio passando a un altro stile dell'overlay).</summary>
    public void RefreshSize()
    {
        Raise(nameof(Size));
        Raise(nameof(SizeText));
    }
}

public sealed class GroupEntry : EntryBase
{
    private readonly Settings _settings;

    public string Key { get; }
    public bool IsHardware { get; }
    /// <summary>Nome del modello (es. "AMD Ryzen 7 9800X3D"), proponibile come etichetta.</summary>
    public string HardwareName { get; }
    public Visibility MoveVis => IsHardware ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>La maniglia per trascinare: nascosta (non tolta) sulle righe fisse, per tenere allineate le altre.</summary>
    public Visibility GripVis => IsHardware ? Visibility.Visible : Visibility.Hidden;

    /// <summary>Per le righe che si possono accendere e spegnere da qui (es. il nome del gioco).</summary>
    public Func<bool>? ShownGetter { get; init; }
    public Action<bool>? ShownSetter { get; init; }
    public Visibility ToggleVis => ShownSetter != null ? Visibility.Visible : Visibility.Collapsed;
    public string Description { get; init; } = "";
    public Visibility DescriptionVis => Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool IsShown
    {
        get => ShownGetter?.Invoke() ?? true;
        set { ShownSetter?.Invoke(value); Raise(); Raise(nameof(RowOpacity)); }
    }
    public double RowOpacity => IsShown ? 1.0 : 0.45;

    // ---- comprimi / espandi le voci del gruppo (solo per la sessione)

    private bool _isExpanded = true;
    private int _childCount;

    /// <summary>Chiamata quando l'utente apre o chiude il gruppo (passa la chiave e il nuovo stato).</summary>
    public Action<string, bool>? ExpandedChanged { get; set; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            Raise(nameof(CountVis));
            Raise(nameof(ExpandTip));
            Raise(nameof(ExpandName));
            ExpandedChanged?.Invoke(Key, value);
        }
    }

    /// <summary>Quante righe figlie ha il gruppo.</summary>
    public int ChildCount
    {
        get => _childCount;
        set
        {
            if (!Set(ref _childCount, value)) return;
            Raise(nameof(ArrowVis));
            Raise(nameof(CountVis));
            Raise(nameof(CountText));
        }
    }

    // Nascosta (non collassata) per tenere l'allineamento anche senza figli.
    public Visibility ArrowVis => ChildCount > 0 ? Visibility.Visible : Visibility.Hidden;
    public Visibility CountVis => !IsExpanded && ChildCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string CountText => ChildCount == 1 ? Loc.T("Entry.OneItem") : string.Format(Loc.T("Entry.Items"), ChildCount);
    public string ExpandTip => Loc.T(IsExpanded ? "Entry.Collapse" : "Entry.Expand");
    public string ExpandName => ExpandTip + " " + Text;

    public GroupEntry(Settings settings, string key, bool isHardware, string defaultLabel, string hardwareName,
        Brush defaultBrush, Action changed)
        : base(defaultLabel, defaultBrush, changed)
    {
        _settings = settings;
        Key = key;
        IsHardware = isHardware;
        HardwareName = hardwareName;
        // Il nome cambia: anche il nome letto dagli strumenti di accessibilità.
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Text)) Raise(nameof(ExpandName)); };
    }

    protected override double? StoredSize
    {
        get => _settings.StyleOf(Key)?.Size;
        set => _settings.StyleFor(Key).Size = value;
    }

    protected override string? StoredText
    {
        get => _settings.StyleOf(Key)?.Label;
        set => _settings.StyleFor(Key).Label = value;
    }

    protected override string? StoredColor
    {
        get => _settings.StyleOf(Key)?.Color;
        set => _settings.StyleFor(Key).Color = value;
    }
}

/// <summary>Una riga che si può solo accendere o spegnere (es. il grafico del frametime).</summary>
public sealed class ToggleEntry : EntryBase
{
    private readonly Func<bool> _get;
    private readonly Action<bool> _set;

    public string Title { get; }
    public string Description { get; }

    public ToggleEntry(string title, string description, Func<bool> get, Action<bool> set)
        : base(title, Brushes.White, () => { })
    {
        Title = title;
        Description = description;
        _get = get;
        _set = set;
    }

    public bool IsShown
    {
        get => _get();
        set { _set(value); Raise(); Raise(nameof(RowOpacity)); }
    }
    public double RowOpacity => IsShown ? 1.0 : 0.45;

    protected override double? StoredSize { get => null; set { } }
    protected override string? StoredText { get => null; set { } }
    protected override string? StoredColor { get => null; set { } }
}

public sealed class SensorEntry : EntryBase
{
    private string _valueText = "";

    public SensorItem Item { get; }
    public string GroupKey { get; }
    /// <summary>Vero per le misure dei frame: si rinominano e si spostano, ma si tolgono dai pulsanti in alto.</summary>
    public bool IsFrame { get; init; }
    public Visibility RemoveVis => IsFrame ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FrameVis => IsFrame ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Spiegazione di cosa misura la voce (solo per le misure dei frame).</summary>
    public string Description { get; init; } = "";

    /// <summary>Acceso/spento nell'overlay. Le misure dei frame restano in elenco anche da spente.</summary>
    public bool IsShown
    {
        get => Item.Hidden != true;
        set
        {
            if (value == IsShown) return;
            Item.Hidden = value ? null : true;
            Raise();
            Raise(nameof(RowOpacity));
            Changed();
        }
    }

    public double RowOpacity => IsShown ? 1.0 : 0.45;

    private Visibility _rowVis = Visibility.Visible;
    /// <summary>Collapsed quando il gruppo a cui appartiene la riga è chiuso.</summary>
    public Visibility RowVis { get => _rowVis; set => Set(ref _rowVis, value); }

    public SensorEntry(SensorItem item, string groupKey, string defaultName, Brush defaultBrush, Action changed)
        : base(defaultName, defaultBrush, changed)
    {
        Item = item;
        GroupKey = groupKey;
    }

    protected override string? StoredText { get => Item.Name; set => Item.Name = value; }
    protected override string? StoredColor { get => Item.Color; set => Item.Color = value; }
    protected override double? StoredSize { get => Item.Size; set => Item.Size = value; }

    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
}
