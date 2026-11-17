using System.Diagnostics;
using System.Text.RegularExpressions;

namespace FrameGrip.Core;

/// <summary>
/// Il driver PawnIO: serve solo per temperatura, frequenza e consumo del processore (e per i sensori
/// della scheda madre), che Windows non espone ai programmi normali. Tutto il resto funziona senza.
/// FrameGrip non lo porta con sé: lo fa scaricare dal suo editore (tramite winget) quando l'utente lo chiede,
/// e sa riparare l'installazione rotta in cui Windows conserva il pacchetto senza il file del driver.
/// </summary>
public static class SensorDriver
{
    public const string WingetId = "namazso.PawnIO";

    /// <summary>Codice di "sc start PawnIO": 0 avviato, 1056 già in funzione, 1060 non installato,
    /// 2 installato ma senza il file del driver, 5 servono i privilegi di amministratore.</summary>
    public static int Start()
    {
        int code = Run("sc.exe", "start PawnIO", 8000, out _);
        AppLog.Write($"Sensors: PawnIO driver start code {code}");
        return code;
    }

    public static bool IsWorking(int code) => code is 0 or 1056;

    /// <summary>Spiegazione breve per l'interfaccia, nella lingua attuale; null se il driver funziona.</summary>
    public static string? Problem(int code) => code switch
    {
        0 or 1056 => null,
        1060 => Loc.T("Driver.NotInstalled"),
        5 => Loc.T("Driver.NeedAdmin"),
        _ => Loc.T("Driver.Damaged"),
    };

    /// <summary>Installa o ripara il driver. Restituisce null se ora funziona, altrimenti l'errore (tradotto quando lo si mostra).</summary>
    public static LocText? InstallOrRepair()
    {
        int code = Start();
        if (IsWorking(code)) return null;
        if (code == 5) return new LocText("Driver.RunAsAdmin");

        if (code != 1060)
        {
            // Installazione rotta: Windows tiene in archivio un pacchetto senza il file del driver e lo
            // riusa a ogni reinstallazione. Va tolto dall'archivio prima di reinstallare.
            Run("pnputil.exe", "/enum-drivers", 20000, out string list);
            foreach (string block in Regex.Split(list, @"\r?\n\s*\r?\n"))
            {
                if (!block.Contains("pawnio.inf", StringComparison.OrdinalIgnoreCase)) continue;
                var oem = Regex.Match(block, @"oem\d+\.inf", RegexOptions.IgnoreCase);
                if (!oem.Success) continue;
                int del = Run("pnputil.exe", $"/delete-driver {oem.Value} /uninstall", 30000, out _);
                AppLog.Write($"Sensors: removed stored driver package {oem.Value}, code {del}");
            }
            int un = Run("winget.exe", $"uninstall --id {WingetId} --silent --accept-source-agreements", 120000, out _);
            AppLog.Write($"Sensors: PawnIO uninstall code {un}");
        }

        int inst = Run("winget.exe",
            $"install --id {WingetId} --source winget --silent --accept-package-agreements --accept-source-agreements", 300000, out _);
        AppLog.Write($"Sensors: PawnIO install code {inst}");

        code = Start();
        if (IsWorking(code)) return null;
        return inst == -1
            ? new LocText("Driver.NoWinget")
            : new LocText("Driver.Failed", inst, code);
    }

    private static int Run(string file, string arguments, int timeoutMs, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            });
            if (p == null) return -1;
            var reader = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return -1; }
            output = reader.Result;
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
