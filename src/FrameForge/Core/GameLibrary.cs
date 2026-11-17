using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FrameGrip.Core;

/// <summary>Un gioco installato da un negozio: nome vero, negozio, codice nel negozio, cartella e (se si sa) l'eseguibile.</summary>
public sealed record LibraryGame(string Name, string Store, string? AppId, string Folder, string? Exe);

/// <summary>
/// I giochi installati da Steam, Game Pass / Microsoft Store, GOG ed Epic, letti dai file e dal registro che i
/// negozi lasciano sul disco. Solo letture: niente rete, niente privilegi, niente file scritti. La scansione costa
/// poco (decine di millisecondi) e gira su un altro thread; si ripete quando si aggancia un gioco sconosciuto.
/// </summary>
public static class GameLibrary
{
    public const string Steam = "Steam", GamePass = "Game Pass", Gog = "GOG", Epic = "Epic";

    private static volatile List<LibraryGame> _games = new();
    private static int _scanning;
    private static long _lastScan;
    public static event Action? Changed;
    public static IReadOnlyList<LibraryGame> Games => _games;
    public static double LastScanMs { get; private set; }
    public static bool Ready { get; private set; }

    /// <summary>Lanciatori e servizi dei negozi: stanno nelle cartelle dei giochi o dei negozi ma non vanno agganciati.</summary>
    private static readonly string[] NotGameExe =
    {
        "steam", "steamwebhelper", "steamservice", "steamerrorreporter", "galaxyclient", "galaxyclient helper", "gogalaxy",
        "epicgameslauncher", "epicwebhelper", "xboxpcapp", "gamebar", "gamebarftserver", "xboxappservices", "gamingservices",
        "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "crashpad_handler", "easyanticheat", "easyanticheat_setup",
        "easyanticheat_eos_setup", "beservice", "battleye", "vc_redist.x64", "vc_redist.x86", "dxsetup", "dotnetfx", "uninstall", "unins000",
    };

    private static readonly string[] JunkExeParts = { "crash", "setup", "unins", "redist", "anticheat", "launcherpatcher", "report", "helper", "dxwebsetup", "vcredist", "install" };

    private static readonly string[] NotGameNames =
    {
        "Steamworks Common Redistributables", "Steam Linux Runtime", "Proton", "SteamVR", "Steam Audio", "Steamworks SDK",
        "Wallpaper Engine", "Source SDK", "Godot Engine", "RPG Maker", "Blender",
    };

    public static bool IsLauncher(string exeNameNoExt) =>
        NotGameExe.Any(n => n.Equals(exeNameNoExt, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scansione in sottofondo (all'avvio, o quando si aggancia un gioco che non si conosce: al massimo ogni 30 s).</summary>
    public static void ScanInBackground(bool force = false)
    {
        long now = Environment.TickCount64;
        if (!force && Ready && now - Interlocked.Read(ref _lastScan) < 30_000) return;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Interlocked.Exchange(ref _lastScan, now);
        Task.Run(() =>
        {
            try { ScanNow(); }
            catch (Exception e) { AppLog.Write("Library: " + e.Message); }
            finally { Interlocked.Exchange(ref _scanning, 0); }
            Changed?.Invoke();
        });
    }

    public static List<LibraryGame> ScanNow()
    {
        var sw = Stopwatch.StartNew();
        var all = new List<LibraryGame>();
        void Safe(string what, Action a) { try { a(); } catch (Exception e) { AppLog.Write($"Library {what}: {e.Message}"); } }
        Safe("Steam", () => all.AddRange(ScanSteam()));
        Safe("GOG", () => all.AddRange(ScanGog()));
        Safe("Game Pass", () => all.AddRange(ScanXbox()));
        Safe("Epic", () => all.AddRange(ScanEpic()));
        // una cartella sola per gioco (lo stesso gioco può comparire da due fonti)
        // marchi nel nome ("Galactic Racer™"): nell'interfaccia non servono
        all = all.Select(g => g with { Name = g.Name.Replace("™", "").Replace("®", "").Replace("©", "").Trim() }).ToList();
        var list = all.GroupBy(g => Norm(g.Folder), StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _games = list;
        LastScanMs = sw.Elapsed.TotalMilliseconds;
        Ready = true;
        AppLog.Write($"Library: {list.Count} games in {LastScanMs:0} ms (" +
            string.Join(", ", list.GroupBy(g => g.Store).Select(g => $"{g.Key} {g.Count()}")) + ")");
        return list;
    }

    private static string Norm(string folder) => Path.GetFullPath(folder).TrimEnd('\\') + "\\";

    /// <summary>Il gioco che contiene questo eseguibile (cartella più specifica), oppure null.</summary>
    public static LibraryGame? Find(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        LibraryGame? best = null;
        int bestLen = 0;
        foreach (var g in _games)
        {
            string f = Norm(g.Folder);
            if (f.Length > bestLen && exePath.StartsWith(f, StringComparison.OrdinalIgnoreCase)) { best = g; bestLen = f.Length; }
        }
        return best ?? FindXboxConfig(exePath);
    }

    /// <summary>Giochi del Microsoft Store installati in WindowsApps (cartella non leggibile per intero): MicrosoftGame.config accanto all'eseguibile.</summary>
    private static LibraryGame? FindXboxConfig(string exePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(exePath);
            for (int up = 0; up < 4 && dir != null; up++, dir = Path.GetDirectoryName(dir))
            {
                string cfg = Path.Combine(dir, "MicrosoftGame.config");
                if (File.Exists(cfg)) return ReadXboxConfig(cfg, dir);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Percorso dell'eseguibile di un processo, anche protetto (anti-cheat), senza privilegi.</summary>
    public static string? ProcessPath(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { Native.CloseHandle(h); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    // ------------------------------------------------------------ Steam

    private static IEnumerable<LibraryGame> ScanSteam()
    {
        var roots = new List<string>();
        void AddRoot(string? p) { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) roots.Add(Path.GetFullPath(p.Replace('/', '\\'))); }
        AddRoot(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        AddRoot(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string);
        var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            libs.Add(r);
            string vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        // solo per le prove: librerie finte (stessa struttura di Steam)
        foreach (var extra in (Environment.GetEnvironmentVariable("FRAMEGRIP_LIBRARY_ROOTS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            libs.Add(extra);

        foreach (var lib in libs)
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                string text;
                try { text = File.ReadAllText(acf); } catch { continue; }
                string? Val(string key) { var m = Regex.Match(text, "\"" + key + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase); return m.Success ? m.Groups[1].Value : null; }
                string? name = Val("name"), dir = Val("installdir"), id = Val("appid");
                if (name == null || dir == null || id == "228980") continue;   // 228980 = Steamworks Common Redistributables
                if (NotGameNames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
                string folder = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(folder)) continue;
                string? exe = MainExe(folder);
                if (exe == null) continue;   // niente eseguibili: è uno strumento o un pacchetto di supporto
                yield return new LibraryGame(name, Steam, id, folder, exe);
            }
        }
    }

    /// <summary>L'eseguibile "principale" di una cartella: il più grande tra quelli che non sono installatori o lanciatori.</summary>
    private static string? MainExe(string folder)
    {
        string? best = null; long bestSize = -1;
        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((folder, 0));
        int seen = 0;
        while (stack.Count > 0 && seen < 400)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.exe").ToList(); } catch { continue; }
            foreach (var f in files)
            {
                seen++;
                string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (IsLauncher(n) || JunkExeParts.Any(n.Contains)) continue;
                long size;
                try { size = new FileInfo(f).Length; } catch { continue; }
                if (size > bestSize) { best = f; bestSize = size; }
            }
            if (depth >= 3) continue;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    string dn = Path.GetFileName(d).ToLowerInvariant();
                    if (dn is "_commonredist" or "redist" or "redistributables" or "directx" or "support" or "__installer" or "easyanticheat" or "battleye") continue;
                    stack.Push((d, depth + 1));
                }
            }
            catch { }
        }
        return best;
    }

    // ------------------------------------------------------------ GOG

    private static IEnumerable<LibraryGame> ScanGog()
    {
        foreach (var root in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var key = Registry.LocalMachine.OpenSubKey(root);
            if (key == null) continue;
            foreach (var id in key.GetSubKeyNames())
            {
                using var g = key.OpenSubKey(id);
                if (g == null) continue;
                string? name = g.GetValue("gameName") as string, path = g.GetValue("path") as string, exe = g.GetValue("exe") as string;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) continue;
                if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe)) exe = MainExe(path);
                if (exe == null) continue;
                yield return new LibraryGame(name.Trim(), Gog, g.GetValue("gameID") as string ?? id, path, exe);
            }
        }
    }

    // ------------------------------------------------------------ Game Pass / Microsoft Store

    private static IEnumerable<LibraryGame> ScanXbox()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            string marker = Path.Combine(drive.RootDirectory.FullName, ".GamingRoot");
            try
            {
                if (File.Exists(marker))
                    foreach (var rel in ReadGamingRoot(File.ReadAllBytes(marker)))
                        roots.Add(Path.Combine(drive.RootDirectory.FullName, rel.TrimStart('\\')));
            }
            catch { }
        }
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); } catch { continue; }
            foreach (var dir in dirs)
            {
                LibraryGame? g = null;
                try
                {
                    foreach (var cfg in new[] { Path.Combine(dir, "Content", "MicrosoftGame.config"), Path.Combine(dir, "MicrosoftGame.config") })
                        if (File.Exists(cfg)) { g = ReadXboxConfig(cfg, Path.GetDirectoryName(cfg)!); break; }
                }
                catch { }
                if (g != null) yield return g;
            }
        }
    }

    /// <summary>.GamingRoot: "RGBX", un contatore, poi i percorsi in UTF-16 terminati da zero.</summary>
    private static IEnumerable<string> ReadGamingRoot(byte[] b)
    {
        if (b.Length < 8 || b[0] != 'R' || b[1] != 'G' || b[2] != 'B' || b[3] != 'X') yield break;
        string s = Encoding.Unicode.GetString(b, 8, (b.Length - 8) & ~1);
        foreach (var part in s.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            if (part.Length > 0) yield return part;
    }

    private static LibraryGame? ReadXboxConfig(string cfg, string contentDir)
    {
        var doc = XDocument.Load(cfg);
        XElement? Find(string local) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == local);
        string? name = Find("ShellVisuals")?.Attribute("DefaultDisplayName")?.Value;
        string? id = Find("Identity")?.Attribute("Name")?.Value;
        string? exeName = Find("Executable")?.Attribute("Name")?.Value;
        string? exe = exeName != null ? Path.Combine(contentDir, exeName) : null;
        string folder = Path.GetFileName(contentDir).Equals("Content", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(contentDir)! : contentDir;
        // nome tradotto in un file di risorse (ms-resource:...): il nome della cartella "XboxGames\<Gioco>" è leggibile
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileName(folder.TrimEnd('\\'));
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new LibraryGame(name.Trim(), GamePass, id, folder, exe);
    }

    // ------------------------------------------------------------ Epic

    private static IEnumerable<LibraryGame> ScanEpic()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var item in Directory.EnumerateFiles(dir, "*.item"))
        {
            LibraryGame? g = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var r = doc.RootElement;
                string? S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool B(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                string? name = S("DisplayName"), loc = S("InstallLocation"), launch = S("LaunchExecutable");
                bool isApp = r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    && cats.EnumerateArray().Any(c => c.GetString() == "applications") && !cats.EnumerateArray().Any(c => c.GetString() == "games");
                if (name != null && loc != null && Directory.Exists(loc) && !B("bIsIncompleteInstall") && !isApp)
                {
                    string? exe = launch != null ? Path.Combine(loc, launch) : null;
                    if (exe == null || !File.Exists(exe)) exe = MainExe(loc);
                    if (exe != null) g = new LibraryGame(name, Epic, S("AppName"), loc, exe);
                }
            }
            catch { }
            if (g != null) yield return g;
        }
    }
}
