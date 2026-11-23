namespace FrameGrip.Core;

/// <summary>
/// Oggetti da chiudere anche se l'app cade per un errore imprevisto: la sessione ETW (resterebbe attiva in Windows)
/// e i collegamenti ai giochi (il limitatore resterebbe acceso nel gioco). Si registrano da soli quando nascono.
/// </summary>
public static class EmergencyCleanup
{
    private static readonly List<WeakReference<IDisposable>> Items = new();

    public static void Register(IDisposable item)
    {
        lock (Items) Items.Add(new WeakReference<IDisposable>(item));
    }

    /// <summary>Chiude tutto il possibile, ognuno per conto suo: un errore in uno non ferma gli altri.</summary>
    public static void DisposeAll()
    {
        IDisposable[] alive;
        lock (Items)
        {
            alive = Items.Select(w => w.TryGetTarget(out var d) ? d : null).OfType<IDisposable>().ToArray();
            Items.Clear();
        }
        foreach (var d in alive)
        {
            try { d.Dispose(); }
            catch (Exception ex) { AppLog.Write($"Cleanup {d.GetType().Name}: {ex.Message}"); }
        }
    }
}
