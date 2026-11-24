using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Miri.Brain;

/// <summary>Captures ONLY a given window rectangle (never the full desktop) as a base64 PNG.</summary>
internal static class ScreenCapture
{
    private const int MaxSide = 1280;   // keep vision prompts cheap on CPU

    public static string? CaptureWindowPngBase64(Rectangle windowBounds)
    {
        var virtualScreen = SystemInformation.VirtualScreen;
        var r = Rectangle.Intersect(windowBounds, virtualScreen);
        if (r.Width < 8 || r.Height < 8) return null;

        using var full = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);

        Bitmap output = full;
        try
        {
            double scale = Math.Min(1.0, (double)MaxSide / Math.Max(r.Width, r.Height));
            if (scale < 1.0)
            {
                var w = Math.Max(1, (int)(r.Width * scale));
                var h = Math.Max(1, (int)(r.Height * scale));
                output = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                using var g2 = Graphics.FromImage(output);
                g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g2.DrawImage(full, 0, 0, w, h);
            }

            using var ms = new MemoryStream();
            output.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        finally
        {
            if (!ReferenceEquals(output, full)) output.Dispose();
        }
    }
}
