using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameGrip.Core;
using Microsoft.Win32;

namespace FrameGrip;

/// <summary>
/// Un gioco della pagina Giochi: installato (libreria di un negozio), con un profilo, o solo giocato una volta.
/// Lo stesso gioco compare una volta sola anche se il profilo, la cronologia e la libreria lo chiamano con
/// eseguibili diversi: tutti i nomi con cui è stato visto finiscono in <see cref="Exes"/>.
/// </summary>
public sealed class GameItem
{
    public string Key { get; init; } = "";          // eseguibile in minuscolo (chiave dei profili nuovi)
    public string Name { get; set; } = "";
    public string? Store { get; set; }
    public string? AppId { get; set; }
    public string? Folder { get; set; }
    public string? ExePath { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool Running { get; set; }
    public HashSet<string> Exes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Il profilo del gioco: per uno dei suoi eseguibili, oppure per la cartella.</summary>
    public (string Key, GameProfile Profile)? Profile(Settings s)
    {
        foreach (var e in Exes.Prepend(Key))
            if (s.GameProfiles.TryGetValue(e, out var p)) return (e, p);
        if (Folder is { Length: > 3 } f)
            foreach (var kv in s.GameProfiles)
                if (kv.Value.Folder is { Length: > 3 } pf && GameCatalog.SameFolder(pf, f)) return (kv.Key, kv.Value);
        return null;
    }

    public List<SavedMeasure> Measures() => MeasureStore.ForKeys(Exes.Append(Key).ToList());

    /// <summary>È il gioco agganciato adesso?</summary>
    public bool Matches(GameInfo? g) =>
        g != null && (Exes.Contains(g.Exe) || string.Equals(Key, g.Exe, StringComparison.OrdinalIgnoreCase)
            || (Folder is { Length: > 3 } f && g.Path != null && g.Path.StartsWith(f.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
}

public static class GameCatalog
{
    public static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tutti i giochi da mostrare: libreria, profili (anche di giochi che la libreria non conosce), cronologia
    /// e il gioco agganciato adesso. Si rifà quando cambia qualcosa: sono poche decine di righe.
    /// </summary>
    public static List<GameItem> Build(Settings s, GameInfo? running)
    {
        var items = new List<GameItem>();
        foreach (var lib in GameLibrary.Games)
        {
            string key = Limits.Key(lib.Exe != null ? Path.GetFileName(lib.Exe) : lib.Name + ".exe");
            if (items.Any(i => SameFolder(i.Folder ?? "", lib.Folder))) continue;
            var it = new GameItem { Key = key, Name = lib.Name, Store = lib.Store, AppId = lib.AppId, Folder = lib.Folder, ExePath = lib.Exe };
            it.Exes.Add(key);
            items.Add(it);
        }
        foreach (var (key, prof) in s.GameProfiles)
        {
            var it = items.FirstOrDefault(i => i.Exes.Contains(key)
                || (prof.Folder is { Length: > 3 } pf && i.Folder != null && SameFolder(pf, i.Folder)));
            if (it == null)
            {
                it = new GameItem { Key = key, Name = prof.DisplayName ?? key, Store = prof.Store, AppId = prof.AppId, Folder = prof.Folder };
                items.Add(it);
            }
            it.Exes.Add(key);
            if (prof.LastSeen > (it.LastSeen ?? DateTime.MinValue)) it.LastSeen = prof.LastSeen;
        }
        foreach (var h in s.GameHistory)
        {
            if (string.IsNullOrEmpty(h.Exe)) continue;
            string key = Limits.Key(h.Exe);
            if (GameLibrary.IsLauncher(Path.GetFileNameWithoutExtension(key))) continue;
            var it = items.FirstOrDefault(i => i.Exes.Contains(key))
                ?? items.FirstOrDefault(i => h.Name != null && string.Equals(i.Name, h.Name, StringComparison.OrdinalIgnoreCase));
            if (it == null)
            {
                it = new GameItem { Key = key, Name = string.IsNullOrWhiteSpace(h.Name) ? key : h.Name, Store = h.Store };
                items.Add(it);
            }
            it.Exes.Add(key);
            if (h.LastSeen > (it.LastSeen ?? DateTime.MinValue)) it.LastSeen = h.LastSeen;
        }
        if (running != null)
        {
            var it = items.FirstOrDefault(i => i.Matches(running));
            if (it == null)
            {
                var lib = GameLibrary.Find(running.Path);
                it = new GameItem { Key = Limits.Key(running.Exe), Name = running.Name, Store = running.Store, AppId = running.AppId, Folder = lib?.Folder, ExePath = running.Path };
                items.Add(it);
            }
            it.Exes.Add(Limits.Key(running.Exe));
            it.Running = true;
        }
        // misure salvate di giochi mai visti altrimenti (per esempio dopo aver cancellato la cronologia)
        foreach (var m in MeasureStore.All)
        {
            if (string.IsNullOrEmpty(m.Exe) || items.Any(i => i.Exes.Contains(m.Exe))) continue;
            var it = new GameItem { Key = Limits.Key(m.Exe), Name = string.IsNullOrWhiteSpace(m.Game) ? m.Exe : m.Game };
            it.Exes.Add(it.Key);
            items.Add(it);
        }
        return items;
    }

    // ------------------------------------------------------------ copertine

    private static readonly Dictionary<string, Task<ImageSource?>> _covers = new();
    // letta una volta sola, anche se più copertine partono insieme
    private static readonly Lazy<string?> _steamRoot = new(() =>
        Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
        ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);

    /// <summary>
    /// La copertina già presente sul disco (cache di Steam, logo di Game Pass), oppure null: niente download.
    /// Si legge in un altro thread; il risultato resta in memoria.
    /// </summary>
    public static Task<ImageSource?> CoverAsync(GameItem g)
    {
        string id = g.Store + "|" + (g.AppId ?? g.Folder ?? g.Key);
        string? store = g.Store, appId = g.AppId, folder = g.Folder;
        // una sola lettura per copertina, anche se l'elenco si rifà mentre si sta ancora leggendo
        lock (_covers)
        {
            if (_covers.TryGetValue(id, out var have)) return have;
            var task = Task.Run(() => Load(id, store, appId, folder));
            _covers[id] = task;
            return task;
        }
    }

    private static ImageSource? Load(string id, string? store, string? appId, string? folder)
    {
        {
            ImageSource? img = null;
            try
            {
                string? file = store switch
                {
                    GameLibrary.Steam when appId != null => SteamCover(appId),
                    GameLibrary.GamePass when folder != null => XboxCover(folder),
                    _ => null,
                };
                if (file != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 460;
                    bmp.UriSource = new Uri(file);
                    bmp.EndInit();
                    bmp.Freeze();
                    img = bmp;
                }
            }
            catch (Exception ex) { AppLog.Write($"Cover {id}: {ex.Message}"); }
            return img;
        }
    }

    private static string? SteamCover(string appId)
    {
        if (_steamRoot.Value is not { } root) return null;
        string cache = Path.Combine(root.Replace('/', '\\'), "appcache", "librarycache");
        // vecchia cache: file sciolti; nuova: una cartella per gioco (a volte con sottocartelle)
        foreach (var n in new[] { "_header.jpg", "_library_hero.jpg", "_library_600x900.jpg" })
        {
            string f = Path.Combine(cache, appId + n);
            if (File.Exists(f)) return f;
        }
        string dir = Path.Combine(cache, appId);
        if (!Directory.Exists(dir)) return null;
        foreach (var n in new[] { "header.jpg", "library_header.jpg", "library_hero.jpg", "library_600x900.jpg" })
        {
            var hit = Directory.EnumerateFiles(dir, n, SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return hit;
        }
        return null;
    }

    private static string? XboxCover(string folder)
    {
        foreach (var cfgDir in new[] { Path.Combine(folder, "Content"), folder })
        {
            string cfg = Path.Combine(cfgDir, "MicrosoftGame.config");
            if (!File.Exists(cfg)) continue;
            var doc = System.Xml.Linq.XDocument.Load(cfg);
            var vis = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
            foreach (var attr in new[] { "Square480x480Logo", "Square150x150Logo", "StoreLogo" })
                if (vis?.Attribute(attr)?.Value is { Length: > 0 } rel && Path.Combine(cfgDir, rel) is var f && File.Exists(f)) return f;
        }
        return null;
    }

    /// <summary>Colori della copertina disegnata quando non c'è un'immagine: sempre gli stessi per lo stesso gioco.</summary>
    public static (Color A, Color B) FallbackColors(string name)
    {
        uint h = 2166136261;
        foreach (char c in name.ToLowerInvariant()) h = (h ^ c) * 16777619;
        double hue = h % 360;
        return (FromHsl(hue, 0.45, 0.30), FromHsl((hue + 40) % 360, 0.55, 0.16));
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0) : h < 120 ? (x, c, 0.0) : h < 180 ? (0.0, c, x) : h < 240 ? (0.0, x, c) : h < 300 ? (x, 0.0, c) : (c, 0.0, x);
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
