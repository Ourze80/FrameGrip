using System.Diagnostics;
using System.Text;

namespace FrameGrip.Core;

/// <summary>
/// Avvio insieme a Windows. FrameGrip gira come amministratore, e Windows non avvia da solo i programmi
/// che chiedono la conferma: per questo si usa un'attività pianificata "all'accesso", che parte già
/// elevata e senza domande. L'app parte nascosta, con la sola icona vicino all'orologio.
/// </summary>
public static class Autostart
{
    public const string TaskName = "FrameGrip Autostart";
    public const string TrayArgument = "--tray";

    public static bool IsEnabled() => Run("schtasks.exe", $"/query /tn \"{TaskName}\"", out _) == 0;

    /// <summary>Accende o spegne l'avvio con Windows. Restituisce null se riuscito, altrimenti l'errore (tradotto quando lo si mostra).</summary>
    public static LocText? Set(bool enabled)
    {
        if (!enabled)
        {
            if (!IsEnabled()) return null;
            return Run("schtasks.exe", $"/delete /tn \"{TaskName}\" /f", out _) == 0
                ? null : new LocText("Autostart.OffFailed");
        }

        string? exe = Environment.ProcessPath;
        if (exe == null) return new LocText("Autostart.NoExe");
        string path = exe.Replace("'", "''");
        string dir = (System.IO.Path.GetDirectoryName(exe) ?? "").Replace("'", "''");
        string script =
            "$u = [Security.Principal.WindowsIdentity]::GetCurrent().Name; " +
            $"$a = New-ScheduledTaskAction -Execute '{path}' -Argument '{TrayArgument}' -WorkingDirectory '{dir}'; " +
            "$t = New-ScheduledTaskTrigger -AtLogOn -User $u; " +
            "$p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; " +
            "$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero); " +
            $"Register-ScheduledTask -TaskName '{TaskName}' -Action $a -Trigger $t -Principal $p -Settings $s -Force -ErrorAction Stop | Out-Null";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        int code = Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", out _);
        return code == 0 && IsEnabled()
            ? null : new LocText("Autostart.SetFailed");
    }

    private static int Run(string file, string arguments, out string output)
    {
        output = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return -1;
            p.ErrorDataReceived += (_, _) => { };
            p.BeginErrorReadLine();
            output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return -1; }
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }
}
