using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Esclusioni scelte dall'utente, nel file excluded-processes.txt accanto alle impostazioni (%APPDATA%\FrameGrip).
/// Due sezioni: [not-games] (nomi di processi, senza .exe, mai agganciati) e [background-paths] (pezzi di percorso
/// di strumenti che avviano copie nascoste di un gioco, per esempio per compilare gli shader in anticipo).
/// Le legge anche la DLL, per tenere fuori il layer Vulkan dagli stessi programmi.
/// Se il file non c'è, se ne scrive uno vuoto con la spiegazione; si legge una volta all'avvio.
/// </summary>
public static class UserExclusions
{
    public const string FileName = "excluded-processes.txt";

    private static readonly Lazy<(HashSet<string> Names, string[] Paths)> Data = new(Load);

    public static IReadOnlySet<string> NotGames => Data.Value.Names;
    public static IReadOnlyList<string> BackgroundPaths => Data.Value.Paths;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameGrip", FileName);

    private const string Template =
        "# FrameGrip: programs that are never treated as games. One entry per line; case does not matter.\n" +
        "# Lines starting with # are ignored. Restart FrameGrip after editing.\n" +
        "\n" +
        "# Process names (without .exe) that are never hooked and never get the overlay.\n" +
        "[not-games]\n" +
        "\n" +
        "# Parts of a path (for example \\MyTool) of background tools that start hidden copies of a game:\n" +
        "# those copies are left alone.\n" +
        "[background-paths]\n";

    private static (HashSet<string>, string[]) Load()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                return (names, paths.ToArray());
            }
            string section = "";
            foreach (var raw in File.ReadAllLines(FilePath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line.ToLowerInvariant(); continue; }
                if (section == "[not-games]") names.Add(line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? line[..^4] : line);
                else if (section == "[background-paths]") paths.Add(line);
            }
        }
        catch (Exception ex) { AppLog.Write("Exclusions: " + ex.Message); }
        return (names, paths.ToArray());
    }
}
