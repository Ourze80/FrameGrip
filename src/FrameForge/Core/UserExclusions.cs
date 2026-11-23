using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Esclusioni scelte dall'utente, nel file excluded-processes.txt accanto alle impostazioni (%APPDATA%\FrameGrip).
/// Tre sezioni: [not-games] (nomi di processi, senza .exe, mai agganciati), [background-paths] (pezzi di percorso
/// di strumenti che avviano copie nascoste di un gioco, per esempio per compilare gli shader in anticipo) e
/// [force-hook] (nomi di processi agganciati anche se sembrano protetti da un anti-cheat, a rischio dell'utente).
/// Le legge anche la DLL, per tenere fuori il layer Vulkan dagli stessi programmi.
/// Se il file non c'è, se ne scrive uno vuoto con la spiegazione; si legge una volta all'avvio.
/// </summary>
public static class UserExclusions
{
    public const string FileName = "excluded-processes.txt";

    private static readonly Lazy<(HashSet<string> Names, string[] Paths, HashSet<string> Force)> Data = new(Load);

    public static IReadOnlySet<string> NotGames => Data.Value.Names;
    public static IReadOnlyList<string> BackgroundPaths => Data.Value.Paths;
    /// <summary>Processi da agganciare anche se l'anti-cheat sembra presente (o se si erano chiusi subito dopo un aggancio).</summary>
    public static IReadOnlySet<string> ForceHook => Data.Value.Force;

    /// <summary>FRAMEGRIP_EXCLUSIONS sceglie un altro file (solo per le prove).</summary>
    public static string FilePath => Environment.GetEnvironmentVariable("FRAMEGRIP_EXCLUSIONS") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameGrip", FileName);

    private const string Template =
        "# FrameGrip: programs that are never treated as games. One entry per line; case does not matter.\n" +
        "# Lines starting with # are ignored. Restart FrameGrip after editing.\n" +
        "\n" +
        "# Process names (without .exe) that are never hooked and never get the overlay.\n" +
        "[not-games]\n" +
        "\n" +
        "# Parts of a path (for example \\MyTool) of background tools that start hidden copies of a game:\n" +
        "# those copies are left alone.\n" +
        "[background-paths]\n" +
        "\n" +
        "# Process names (without .exe) hooked even if an anti-cheat is detected or the game closed right after hooking.\n" +
        "# WARNING: injecting into a game protected by an anti-cheat can get your account banned. Use at your own risk.\n" +
        "[force-hook]\n";

    private static (HashSet<string>, string[], HashSet<string>) Load()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var force = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        try
        {
            if (!File.Exists(FilePath))
            {
                // le prove automatiche non scrivono nella cartella dell'utente
                if (Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is not { Length: > 0 })
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    File.WriteAllText(FilePath, Template.Replace("\n", Environment.NewLine));
                }
                return (names, paths.ToArray(), force);
            }
            string section = "";
            var lines = File.ReadAllLines(FilePath);
            bool hasForce = false;
            foreach (var raw in lines)
            {
                if (raw.Trim().Equals("[force-hook]", StringComparison.OrdinalIgnoreCase)) hasForce = true;
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line.ToLowerInvariant(); continue; }
                string exe = line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? line[..^4] : line;
                if (section == "[not-games]") names.Add(exe);
                else if (section == "[background-paths]") paths.Add(line);
                else if (section == "[force-hook]") force.Add(exe);
            }
            // File scritto da una versione precedente: si aggiunge in fondo la sezione nuova, con la sua spiegazione.
            if (!hasForce && Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS") is not { Length: > 0 })
            {
                int at = Template.IndexOf("\n# Process names (without .exe) hooked even", StringComparison.Ordinal);
                File.AppendAllText(FilePath, Template[at..].Replace("\n", Environment.NewLine));
            }
        }
        catch (Exception ex) { AppLog.Write("Exclusions: " + ex.Message); }
        return (names, paths.ToArray(), force);
    }
}
