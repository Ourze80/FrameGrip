using System.Diagnostics;
using System.IO;

namespace FrameGrip.Core;

/// <summary>
/// Decide se il programma in primo piano è davvero un gioco, prima di agganciarlo.
/// Agganciare un programma qualunque (un editor, un lettore, un'app di Windows) può rovinarne la grafica,
/// quindi nel dubbio si lascia stare: l'overlay resta nella finestra esterna.
/// </summary>
public static class GameDetector
{
    /// <summary>Secondi consecutivi di rendering continuo richiesti prima di agganciare.</summary>
    public const int SteadySeconds = 3;
    /// <summary>Sotto questa soglia il programma non sta "giocando": disegna solo quando cambia qualcosa.</summary>
    public const double MinFps = 15;

    /// <summary>Librerie di interfaccia che un gioco non carica: chi le ha è un'applicazione normale.</summary>
    private static readonly string[] AppModules =
    {
        "wpfgfx_cor3.dll", "wpfgfx_v0400.dll", "presentationcore.dll", "presentationnative_cor3.dll",
        "microsoft.ui.xaml.dll", "flutter_windows.dll", "avalonia.base.dll", "av_libglesv2.dll",
    };

    private static readonly Dictionary<int, string?> Cache = new();

    /// <summary>Null se può essere un gioco, altrimenti la chiave del motivo per cui non lo si aggancia (vedi Strings, "Skip.*").</summary>
    public static string? WhyNot(int pid, IntPtr window)
    {
        string? windowReason = CheckWindow(window);
        if (windowReason != null) return windowReason;   // la finestra cambia: non si mette in memoria

        lock (Cache)
        {
            if (Cache.TryGetValue(pid, out var known)) return known;
            if (Cache.Count > 64) Cache.Clear();
            return Cache[pid] = CheckProcess(pid);
        }
    }

    private static string? CheckWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !Native.IsWindowVisible(window)) return "Skip.NoWindow";
        long ex = Native.GetWindowLongPtr(window, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return "Skip.ToolWindow";
        if (!Native.GetWindowRect(window, out var r)) return "Skip.NoWindow";
        if (r.Right - r.Left < 320 || r.Bottom - r.Top < 240) return "Skip.TooSmall";
        return null;
    }

    private static string? CheckProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? path = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (path.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase)) return "Skip.Windows";

                // Strumenti che avviano copie nascoste del gioco (per esempio per compilare gli shader in anticipo):
                // non sono partite, e agganciarle disturberebbe il loro lavoro. L'elenco lo scrive l'utente.
                foreach (var part in UserExclusions.BackgroundPaths)
                    if (path.Contains(part, StringComparison.OrdinalIgnoreCase)) return "Skip.Background";

                // Le app Electron (Discord, VS Code, Slack...) hanno il loro pacchetto accanto all'eseguibile.
                string dir = Path.GetDirectoryName(path) ?? "";
                if (File.Exists(Path.Combine(dir, "resources", "app.asar")) || Directory.Exists(Path.Combine(dir, "resources", "app")))
                    return "Skip.Electron";
            }

            foreach (ProcessModule m in p.Modules)
            {
                string name = m.ModuleName ?? "";
                foreach (string app in AppModules)
                    if (name.Equals(app, StringComparison.OrdinalIgnoreCase)) return "Skip.DesktopApp";
            }
        }
        catch
        {
            // Processo protetto (anti-cheat) o appena chiuso: non si può guardare dentro.
            // I giochi protetti sono proprio di questo tipo, quindi non lo si esclude.
        }
        return null;
    }
}
