using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfSize = System.Windows.Size;

namespace Miri.App;

/// <summary>
/// A pixel-art panel: stepped corners, 1-"pixel" outline, shade on the right/bottom, optional
/// speech-bubble tail at the bottom or top (TailX, relative to the panel's left edge; NaN = no tail; TailOnTop).
/// </summary>
public sealed class PixelPanel : Decorator
{
    public static readonly DependencyProperty PixelProperty = DependencyProperty.Register(
        nameof(Pixel), typeof(double), typeof(PixelPanel),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(PixelPanel),
        new FrameworkPropertyMetadata(new Thickness(8), FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(WpfBrush), typeof(PixelPanel),
        new FrameworkPropertyMetadata(Palette.Fur, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
        nameof(Outline), typeof(WpfBrush), typeof(PixelPanel),
        new FrameworkPropertyMetadata(Palette.Outline, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShadeProperty = DependencyProperty.Register(
        nameof(Shade), typeof(WpfBrush), typeof(PixelPanel),
        new FrameworkPropertyMetadata(Palette.Shade, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TailXProperty = DependencyProperty.Register(
        nameof(TailX), typeof(double), typeof(PixelPanel),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public double Pixel { get => (double)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    public Thickness Padding { get => (Thickness)GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public WpfBrush Fill { get => (WpfBrush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public WpfBrush Outline { get => (WpfBrush)GetValue(OutlineProperty); set => SetValue(OutlineProperty, value); }
    public WpfBrush Shade { get => (WpfBrush)GetValue(ShadeProperty); set => SetValue(ShadeProperty, value); }
    public static readonly DependencyProperty TailOnTopProperty = DependencyProperty.Register(
        nameof(TailOnTop), typeof(bool), typeof(PixelPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsArrange | FrameworkPropertyMetadataOptions.AffectsRender));

    public double TailX { get => (double)GetValue(TailXProperty); set => SetValue(TailXProperty, value); }
    /// <summary>Tail at the top pointing up (bubble sits below the kitten) instead of at the bottom.</summary>
    public bool TailOnTop { get => (bool)GetValue(TailOnTopProperty); set => SetValue(TailOnTopProperty, value); }

    private bool HasTail => !double.IsNaN(TailX);
    private double TailHeight => HasTail ? Pixel * 2 : 0;

    protected override WpfSize MeasureOverride(WpfSize available)
    {
        double p = Pixel;
        var pad = Padding;
        double extraW = 2 * p + pad.Left + pad.Right + p;      // + shade column
        double extraH = 2 * p + pad.Top + pad.Bottom + p + TailHeight;
        if (Child == null) return new WpfSize(extraW, extraH);
        Child.Measure(new WpfSize(Math.Max(0, available.Width - extraW), Math.Max(0, available.Height - extraH)));
        return new WpfSize(Child.DesiredSize.Width + extraW, Child.DesiredSize.Height + extraH);
    }

    protected override WpfSize ArrangeOverride(WpfSize final)
    {
        double p = Pixel;
        var pad = Padding;
        Child?.Arrange(new Rect(p + pad.Left, p + pad.Top + (TailOnTop ? TailHeight : 0),
            Math.Max(0, final.Width - 3 * p - pad.Left - pad.Right),
            Math.Max(0, final.Height - 3 * p - pad.Top - pad.Bottom - TailHeight)));
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double p = Pixel, w = Snap(ActualWidth), h = Snap(ActualHeight - TailHeight);
        if (w < 5 * p || h < 5 * p) return;
        var fill = Fill; var o = Outline; var s = Shade;
        bool top = HasTail && TailOnTop;
        if (top) dc.PushTransform(new TranslateTransform(0, TailHeight));   // body below the tail

        // body fill (stepped corners)
        dc.DrawRectangle(fill, null, new Rect(p, 2 * p, w - 2 * p, h - 4 * p));
        dc.DrawRectangle(fill, null, new Rect(2 * p, p, w - 4 * p, h - 2 * p));
        // shade: right column + bottom row inside the outline
        dc.DrawRectangle(s, null, new Rect(w - 2 * p, 2 * p, p, h - 4 * p));
        dc.DrawRectangle(s, null, new Rect(2 * p, h - 2 * p, w - 4 * p, p));
        // outline
        dc.DrawRectangle(o, null, new Rect(2 * p, 0, w - 4 * p, p));
        dc.DrawRectangle(o, null, new Rect(2 * p, h - p, w - 4 * p, p));
        dc.DrawRectangle(o, null, new Rect(0, 2 * p, p, h - 4 * p));
        dc.DrawRectangle(o, null, new Rect(w - p, 2 * p, p, h - 4 * p));
        foreach (var (cx, cy) in new[] { (p, p), (w - 2 * p, p), (p, h - 2 * p), (w - 2 * p, h - 2 * p) })
            dc.DrawRectangle(o, null, new Rect(cx, cy, p, p));
        if (top) dc.Pop();

        if (HasTail)
        {
            // the top tail is the bottom one mirrored onto the body's top edge (y -> h + TailHeight - y)
            if (top) dc.PushTransform(new MatrixTransform(1, 0, 0, -1, 0, h + TailHeight));
            double tx = Snap(Math.Clamp(TailX, 5 * p, w - 5 * p));
            // open the bottom outline, then a stepped tail pointing down
            dc.DrawRectangle(fill, null, new Rect(tx - 2 * p, h - 2 * p, 4 * p, 2 * p));
            dc.DrawRectangle(o, null, new Rect(tx - 3 * p, h - p, p, p));
            dc.DrawRectangle(o, null, new Rect(tx + 2 * p, h - p, p, p));
            dc.DrawRectangle(o, null, new Rect(tx - 2 * p, h, p, p));
            dc.DrawRectangle(fill, null, new Rect(tx - p, h, 2 * p, p));
            dc.DrawRectangle(s, null, new Rect(tx + p - p, h, p, p));
            dc.DrawRectangle(o, null, new Rect(tx + p, h, p, p));
            dc.DrawRectangle(o, null, new Rect(tx - p, h + p, 2 * p, p));
            if (top) dc.Pop();
        }
    }

    private double Snap(double v) => Math.Floor(v / Pixel) * Pixel;
}

internal static class Palette
{
    public static readonly SolidColorBrush Fur = Make(0xF4, 0xF4, 0xF2);
    public static readonly SolidColorBrush Shade = Make(0xC9, 0xCD, 0xD6);
    public static readonly SolidColorBrush Outline = Make(0x5A, 0x5E, 0x6B);
    public static readonly SolidColorBrush Eyes = Make(0xB7, 0xB8, 0x6A);
    public static readonly SolidColorBrush Pink = Make(0xE8, 0xA9, 0xA6);
    public static readonly SolidColorBrush Danger = Make(0xD2, 0x45, 0x45);
    public static readonly SolidColorBrush DangerShade = Make(0xF2, 0xC4, 0xC1);
    public static readonly SolidColorBrush Text = Make(0x3A, 0x3D, 0x47);
    public static readonly SolidColorBrush Muted = Make(0x8A, 0x8F, 0x9C);

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
