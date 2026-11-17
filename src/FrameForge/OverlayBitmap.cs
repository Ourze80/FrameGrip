using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrameGrip.Core;

namespace FrameGrip;

/// <summary>
/// Disegna l'overlay in un'immagine da passare al gioco. Così stili, temi e caratteri
/// sono identici dentro il gioco e nell'anteprima, con qualsiasi API grafica.
/// La linea del grafico non c'è: la aggiunge OverlayPump 60 volte al secondo (GraphRaster).
/// Due strati: quello fisso (sfondo, etichette, nomi, unità, cornici) si ridisegna solo quando cambia;
/// a ogni aggiornamento si disegnano solo i valori e si sovrappongono al fisso.
/// </summary>
public sealed class OverlayBitmap
{
    private readonly OverlayView _view = new() { OmitGraphLine = true };
    private RenderTargetBitmap? _fixedTarget, _changingTarget;
    private byte[] _fixed = Array.Empty<byte>(), _changing = Array.Empty<byte>();
    private string _fixedSignature = "", _changingSignature = "";

    /// <summary>Falso se l'ultimo Render ha trovato tutto uguale alla volta prima (Pixels non è cambiato).</summary>
    public bool Changed { get; private set; }

    public byte[] Pixels { get; private set; } = Array.Empty<byte>();
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Riquadro del grafico in pixel dell'immagine (vuoto se il grafico non c'è).</summary>
    public Rect GraphRect { get; private set; } = Rect.Empty;
    /// <summary>Pixel dell'immagine per unità WPF.</summary>
    public double Scale { get; private set; } = 1;
    public (Color Line, double LineWidth, Color Grid, double GridWidth) GraphInk => _view.GraphInk;

    /// <summary>Restituisce falso se non c'è nulla da mostrare.</summary>
    public bool Render(Settings settings, OverlayData data, double dpiScale)
    {
        if (_view.Settings != settings) _view.Settings = settings;
        _view.RenderLayer = OverlayView.Layer.Changing;
        _view.Update(data);
        _view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _view.DesiredSize;
        if (size.Width < 1 || size.Height < 1) return false;

        double k = settings.Scale * dpiScale;
        k = Math.Min(k, Math.Min(GameHook.MaxOverlayWidth / size.Width, GameHook.MaxOverlayHeight / size.Height));
        int w = Math.Max(1, (int)Math.Floor(size.Width * k));
        int h = Math.Max(1, (int)Math.Floor(size.Height * k));
        int bytes = w * h * 4;

        // Se nulla di ciò che si vede è cambiato (i sensori cambiano una volta al secondo, i testi degli FPS
        // spesso restano uguali) non si ridisegna niente: è la parte cara.
        string changing = _view.ChangingSignature() + "|" + w + "x" + h + "|" + _view.FixedSignature;
        Changed = changing != _changingSignature || Pixels.Length < bytes;
        if (!Changed) return true;
        _changingSignature = changing;
        _view.InvalidateVisual();   // la vista non si è invalidata da sola se cambia solo la scritta "max"
        _view.Arrange(new Rect(size));

        // Strato fisso: solo se è cambiato qualcosa che ci sta dentro (o la misura).
        string signature = _view.FixedSignature + "|" + w + "x" + h;
        if (signature != _fixedSignature || _fixed.Length < bytes)
        {
            DrawLayer(OverlayView.Layer.Fixed, size, ref _fixedTarget, ref _fixed, w, h, k);
            _fixedSignature = signature;
        }
        DrawLayer(OverlayView.Layer.Changing, size, ref _changingTarget, ref _changing, w, h, k);

        if (Pixels.Length < bytes) Pixels = new byte[bytes];
        Compose(_changing, _fixed, Pixels, bytes);
        Width = w;
        Height = h;
        Scale = k;
        var g = _view.LastGraphRect;
        GraphRect = g.IsEmpty ? Rect.Empty : new Rect(g.X * k, g.Y * k, g.Width * k, g.Height * k);
        return true;
    }

    private void DrawLayer(OverlayView.Layer layer, Size size, ref RenderTargetBitmap? target, ref byte[] pixels, int w, int h, double k)
    {
        // Arrange richiama OnRender solo se la vista è invalidata: si cambia strato e si ridisegna.
        if (_view.RenderLayer != layer)
        {
            _view.RenderLayer = layer;
            _view.InvalidateVisual();
            _view.Arrange(new Rect(size));
        }
        if (target == null || target.PixelWidth != w || target.PixelHeight != h)
            target = new RenderTargetBitmap(w, h, 96 * k, 96 * k, PixelFormats.Pbgra32);
        else
            target.Clear();
        target.Render(_view);
        int bytes = w * h * 4;
        if (pixels.Length < bytes) pixels = new byte[bytes];
        target.CopyPixels(pixels, w * 4, 0);
    }

    /// <summary>top sopra bottom, entrambi BGRA premoltiplicato.</summary>
    private static void Compose(byte[] top, byte[] bottom, byte[] dst, int bytes)
    {
        for (int i = 0; i < bytes; i += 4)
        {
            int a = top[i + 3];
            if (a == 0)
            {
                dst[i] = bottom[i]; dst[i + 1] = bottom[i + 1]; dst[i + 2] = bottom[i + 2]; dst[i + 3] = bottom[i + 3];
                continue;
            }
            int inv = 255 - a;
            dst[i] = (byte)Math.Min(255, top[i] + (bottom[i] * inv + 127) / 255);
            dst[i + 1] = (byte)Math.Min(255, top[i + 1] + (bottom[i + 1] * inv + 127) / 255);
            dst[i + 2] = (byte)Math.Min(255, top[i + 2] + (bottom[i + 2] * inv + 127) / 255);
            dst[i + 3] = (byte)Math.Min(255, a + (bottom[i + 3] * inv + 127) / 255);
        }
    }
}
