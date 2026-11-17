using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace FrameGrip;

/// <summary>
/// Traduzioni dell'interfaccia (inglese e italiano), cambiabili mentre l'app è aperta.
/// I testi stanno in <see cref="Strings"/>; una chiave mancante ripiega sull'inglese, e se manca anche
/// quello si mostra la chiave stessa: non deve mai far cadere l'app.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    private volatile string _language = "en";

    /// <summary>"en" oppure "it".</summary>
    public string Language
    {
        get => _language;
        set
        {
            string v = Normalize(value);
            if (v == _language) return;
            _language = v;
            // "Item[]" aggiorna tutte le scritte legate con {local:T ...}.
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
            LanguageChanged?.Invoke();
        }
    }

    /// <summary>Per le parti costruite nel codice, che vanno rifatte quando cambia la lingua.</summary>
    public static event Action? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => T(key);

    public static bool IsItalian => Instance._language == "it";

    public static string Normalize(string? language) => language == "it" ? "it" : "en";

    /// <summary>Lingua predefinita quando l'utente non l'ha ancora scelta: quella di Windows, se è l'italiano.</summary>
    public static string SystemDefault()
    {
        try { return CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "it"
                  || CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it" ? "it" : "en"; }
        catch { return "en"; }
    }

    /// <summary>Il testo nella lingua attuale.</summary>
    public static string T(string key)
    {
        if (!Strings.All.TryGetValue(key, out var s)) return key;
        return IsItalian && !string.IsNullOrEmpty(s.It) ? s.It : s.En;
    }

    /// <summary>Il testo nella lingua attuale, con i valori al posto di {0}, {1}...</summary>
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(T(key), args); }
        catch (FormatException) { return T(key); }
    }

    /// <summary>Sempre in inglese: per i file di registro.</summary>
    public static string En(string key, params object?[] args)
    {
        string text = Strings.All.TryGetValue(key, out var s) ? s.En : key;
        if (args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }
}

/// <summary>
/// Un messaggio da tradurre al momento in cui lo si mostra: chiave più valori.
/// Le classi di Core lo restituiscono così il testo segue la lingua anche dopo un cambio.
/// </summary>
public sealed class LocText
{
    private readonly string? _key;
    private readonly string? _raw;
    private readonly object?[] _args;

    public LocText(string key, params object?[] args)
    {
        _key = key;
        _args = args;
    }

    private LocText(string raw)
    {
        _raw = raw;
        _args = Array.Empty<object?>();
    }

    /// <summary>Testo già pronto che non si traduce (per esempio il messaggio di un'eccezione di Windows).</summary>
    public static LocText Raw(string text) => new(text);

    public override string ToString() => _raw ?? Loc.F(_key!, _args);

    /// <summary>Lo stesso messaggio in inglese, per i file di registro.</summary>
    public string English => _raw ?? Loc.En(_key!, _args);
}

/// <summary>
/// Scritta tradotta in XAML: Text="{local:T Options.Title}". Segue i cambi di lingua.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) { Key = key; }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}

/// <summary>
/// Traduce un valore che arriva da un elenco (es. il nome di un tema dell'overlay) aggiungendo un prefisso
/// alla chiave. Il secondo valore è la lingua, così la scritta si aggiorna quando cambia.
/// </summary>
public sealed class LocKeyConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        string name = values.Length > 0 ? values[0] as string ?? "" : "";
        string key = (parameter as string ?? "") + name;
        string text = Loc.T(key);
        return text == key ? name : text;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
