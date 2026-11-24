using System.Windows;
using System.Windows.Media;

namespace Miri.Pet;

/// <summary>
/// Converts between physical pixels (Win32 / UI Automation, the app is PerMonitorV2-aware)
/// and WPF device-independent pixels. Miri lives on the primary monitor, so one scale is used.
/// </summary>
public static class ScreenMetrics
{
    private static double _scale = SystemScale();

    /// <summary>Physical pixels per DIP (1.25 at 125 %).</summary>
    public static double Scale => _scale;

    public static event Action? ScaleChanged;

    /// <summary>Takes the DPI from a shown visual (call after SourceInitialized and on DpiChanged).</summary>
    public static void UpdateFrom(Visual visual)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(visual);
            SetScale(dpi.DpiScaleX);
        }
        catch { /* not connected yet */ }
    }

    public static void SetScale(double scale)
    {
        if (scale <= 0 || Math.Abs(scale - _scale) < 0.001) return;
        _scale = scale;
        ScaleChanged?.Invoke();
    }

    private static double SystemScale()
    {
        try { return Math.Max(1.0, NativeMethods.GetDpiForSystem() / 96.0); }
        catch { return 1.0; }
    }

    public static double ToDip(double px) => px / _scale;
    public static double ToPx(double dip) => dip * _scale;

    public static Point ToDip(int x, int y) => new(x / _scale, y / _scale);

    public static Rect ToDip(System.Drawing.Rectangle r) =>
        new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

    /// <summary>Cursor position in DIPs.</summary>
    public static Point CursorDip()
    {
        NativeMethods.GetCursorPos(out var p);
        return ToDip(p.X, p.Y);
    }

    /// <summary>Primary work area (screen minus taskbar) in DIPs.</summary>
    public static Rect WorkAreaDip()
    {
        if (NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, out var r, 0))
            return new Rect(r.Left / _scale, r.Top / _scale, (r.Right - r.Left) / _scale, (r.Bottom - r.Top) / _scale);
        return SystemParameters.WorkArea;
    }
}
