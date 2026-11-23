using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace FrameGrip;

public partial class App : Application
{
    private const string MutexName = @"Local\FrameGrip.SingleInstance";
    private const string ShowEventName = @"Local\FrameGrip.ShowWindow";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    // Errori imprevisti dell'interfaccia: si registrano e l'app continua, ma se si ripetono a raffica
    // (più di MaxCrashes in un minuto) c'è un problema che non passa, e si lascia chiudere l'app.
    private const int MaxCrashes = 5;
    private readonly Queue<long> _crashTimes = new();

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            Core.AppLog.Write("Crash (UI): " + ev.Exception);
            long now = Environment.TickCount64;
            lock (_crashTimes)
            {
                _crashTimes.Enqueue(now);
                while (_crashTimes.Count > 0 && now - _crashTimes.Peek() > 60_000) _crashTimes.Dequeue();
                if (_crashTimes.Count > MaxCrashes)
                {
                    Core.AppLog.Write($"Crash (UI): more than {MaxCrashes} errors in a minute, closing");
                    Core.EmergencyCleanup.DisposeAll();
                    return;   // non gestito: l'app si chiude
                }
            }
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            Core.AppLog.Write($"Crash (fatal{(ev.IsTerminating ? "" : ", not terminating")}): {ev.ExceptionObject}");
            // L'app sta per chiudersi: la sessione ETW e il limitatore nei giochi non devono restare attivi.
            Core.EmergencyCleanup.DisposeAll();
        };
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Core.AppLog.Write("Crash (background task): " + ev.Exception);
            ev.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashHandlers();
        // Avviata dall'attività pianificata (avvio automatico) l'app nasce a priorità bassa: si torna alla normale.
        if (Native.RestoreNormalPriority() is { } priority) Core.AppLog.Write("Priority: " + priority);
        // Una sola copia alla volta: due copie si contendono il layer Vulkan e i tasti rapidi.
        // Le prove automatiche (FRAMEGRIP_SETTINGS) possono affiancarsi alla copia dell'utente.
        bool testRun = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FRAMEGRIP_SETTINGS"));
        // Solo per le prove: controlla l'analisi del limite ideale su tracce sintetiche, scrive l'esito ed esce.
        if (Environment.GetEnvironmentVariable("FRAMEGRIP_IDEAL_SELFTEST") is { Length: > 0 } selfTest)
            Environment.Exit(Core.IdealLimit.SelfTest(selfTest) ? 0 : 1);
        // Solo per le prove: elenco dei giochi installati trovati (per negozio) e tempi di scansione, poi esce.
        if (Environment.GetEnvironmentVariable("FRAMEGRIP_LIBRARY_DUMP") is { Length: > 0 } dump)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cold = Core.GameLibrary.ScanNow();
            double coldMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Core.GameLibrary.ScanNow();
            double warmMs = sw.Elapsed.TotalMilliseconds;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"scan: first {coldMs:0} ms, second {warmMs:0} ms, {cold.Count} games");
            foreach (var g in cold.GroupBy(x => x.Store)) sb.AppendLine($"{g.Key}: {g.Count()}");
            foreach (var g in cold) sb.AppendLine($"[{g.Store}] {g.Name} | id {g.AppId} | {g.Folder} | {g.Exe}");
            string? probe = Environment.GetEnvironmentVariable("FRAMEGRIP_LIBRARY_PROBE");
            if (!string.IsNullOrEmpty(probe))
                foreach (var p in probe.Split(';')) sb.AppendLine($"probe {p} -> {Core.GameLibrary.Find(p)?.ToString() ?? "not found"}");
            System.IO.File.WriteAllText(dump, sb.ToString());
            Environment.Exit(0);
        }
        if (!testRun && !AcquireSingleInstance())
        {
            SignalRunningInstance();
            Environment.Exit(0);
        }

        // Lingua e tema prima che si apra la prima finestra: chi usa il tema chiaro non vede un lampo scuro.
        StartupSettings = Core.Settings.Load();
        Loc.Instance.Language = StartupSettings.Language;
        Theming.Apply(StartupSettings.AppTheme, StartupSettings.AppAccent);
        base.OnStartup(e);

        // La finestra principale si crea qui (non con StartupUri) per poterla tenere nascosta.
        var w = new FrameGrip.MainWindow();
        MainWindow = w;
        if (FrameGrip.MainWindow.StartsInTray)
        {
            // Avvio insieme a Windows: la finestra si crea (icona, tasti rapidi, messaggi) ma non si mostra.
            // Non la si apre "ridotta a icona": Windows la terrebbe ridotta anche dopo averla nascosta.
            new System.Windows.Interop.WindowInteropHelper(w).EnsureHandle();
        }
        else
        {
            w.Show();
        }
    }

    /// <summary>Le impostazioni lette all'avvio: la finestra principale usa queste, senza rileggere il file.</summary>
    public static Core.Settings? StartupSettings { get; private set; }

    private bool AcquireSingleInstance()
    {
        try
        {
            _mutex = new Mutex(true, MutexName, out bool createdNew);
            if (!createdNew) return false;
        }
        catch (UnauthorizedAccessException)
        {
            // L'altra copia gira come amministratore: esiste ma non possiamo aprirla.
            return false;
        }
        catch (AbandonedMutexException)
        {
            // La copia precedente è stata chiusa di forza: il blocco ora è nostro.
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(WaitForShowRequests) { IsBackground = true, Name = "FrameGrip.ShowWindow" };
            thread.Start();
        }
        catch
        {
            // Senza il segnale la seconda copia si chiude soltanto, senza portare avanti la finestra.
        }
        return true;
    }

    private void WaitForShowRequests()
    {
        while (_showEvent!.WaitOne())
            Dispatcher.BeginInvoke(BringMainWindowToFront);
    }

    private void BringMainWindowToFront()
    {
        if (MainWindow is FrameGrip.MainWindow w) w.BringToFront();
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
        }
        catch
        {
            // La prima copia non è raggiungibile (per esempio gira come amministratore).
        }
    }
}
