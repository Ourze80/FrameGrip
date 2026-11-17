# Disegna il logo di FrameGrip e ne ricava l'icona dell'app.
# Il simbolo: due "ganasce" che stringono tre barre uguali, cioè fotogrammi tenuti a ritmo costante.
# Produce: src\FrameForge\Assets\app.ico (icona, più misure) e src\branding\logo.png (512 px).
# Uso: .\make-logo.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class FrameGripLogo
{
    // Tutto è disegnato su una griglia 256x256 e poi scalato alla misura richiesta.
    public static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.ScaleTransform(size / 256f, size / 256f);

            // Sfondo: quadrato scuro con angoli arrotondati.
            using (var back = Rounded(8, 8, 240, 240, 56))
            using (var fill = new LinearGradientBrush(new Point(0, 8), new Point(0, 248), Color.FromArgb(0x1B, 0x20, 0x29), Color.FromArgb(0x0C, 0x0E, 0x12)))
                g.FillPath(fill, back);

            // Ganasce: due parentesi squadrate spesse, dal blu al turchese.
            using (var grip = new LinearGradientBrush(new Point(48, 56), new Point(208, 200), Color.FromArgb(0x4C, 0x9B, 0xFF), Color.FromArgb(0x3D, 0xD6, 0xD0)))
            using (var pen = new Pen(grip, size < 32 ? 30 : 24))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] { new PointF(92, 60), new PointF(58, 60), new PointF(58, 196), new PointF(92, 196) });
                g.DrawLines(pen, new[] { new PointF(164, 60), new PointF(198, 60), new PointF(198, 196), new PointF(164, 196) });
            }

            // Fotogrammi: tre barre bianche tutte uguali (nelle misure piccole una sola, più larga).
            using (var white = new SolidBrush(Color.FromArgb(0xF2, 0xF4, 0xF7)))
            {
                if (size < 32)
                {
                    using (var bar = Rounded(110, 92, 36, 72, 12)) g.FillPath(white, bar);
                }
                else
                {
                    foreach (int x in new[] { 96, 120, 144 })
                        using (var bar = Rounded(x, 92, 16, 72, 8)) g.FillPath(white, bar);
                }
            }
        }
        return bmp;
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // File .ico: la misura da 256 è salvata come PNG, le altre come bitmap classiche a 32 bit
    // (il formato che ogni strumento di compilazione accetta).
    public static void SaveIcon(string path, int[] sizes)
    {
        var images = new List<byte[]>();
        foreach (int s in sizes)
        {
            using (var bmp = Render(s))
            {
                if (s >= 256)
                {
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); images.Add(ms.ToArray()); }
                    continue;
                }
                var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                var pixels = new byte[s * s * 4];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                bmp.UnlockBits(data);
                int maskRow = ((s + 31) / 32) * 4;
                using (var ms = new MemoryStream())
                using (var w = new BinaryWriter(ms))
                {
                    w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
                    w.Write(0); w.Write(s * s * 4 + maskRow * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                    for (int y = s - 1; y >= 0; y--) w.Write(pixels, y * s * 4, s * 4);   // righe dal basso verso l'alto
                    w.Write(new byte[maskRow * s]);                                       // maschera vuota: vale l'alfa
                    images.Add(ms.ToArray());
                }
            }
        }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

$here = $PSScriptRoot
$assets = Join-Path $here '..\FrameForge\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
[FrameGripLogo]::SaveIcon((Join-Path $assets 'app.ico'), @(256, 64, 48, 32, 24, 16))
$png = [FrameGripLogo]::Render(512); $png.Save((Join-Path $here 'logo.png')); $png.Dispose()
'Logo creato: ' + (Resolve-Path (Join-Path $assets 'app.ico'))
