using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Miri.App;

/// <summary>Click-through overlay that draws a pulsing pixel frame around an approval / pointing target.</summary>
public sealed class HighlightWindow : Window
{
    private const double Pad = 6;
    private readonly WpfRectangle _outer;
    private readonly WpfRectangle _inner;
    private readonly Grid _root;
    private readonly DispatcherTimer _autoHide;

    public HighlightWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _outer = new WpfRectangle { Stroke = Palette.Outline, StrokeThickness = 6, SnapsToDevicePixels = true };
        _inner = new WpfRectangle { Stroke = Palette.Pink, StrokeThickness = 3, Margin = new Thickness(1.5), SnapsToDevicePixels = true };
        _root = new Grid();
        _root.Children.Add(_outer);
        _root.Children.Add(_inner);
        Content = _root;

        _autoHide = new DispatcherTimer();
        _autoHide.Tick += (_, _) => HideHighlight();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.AddExStyle(new WindowInteropHelper(this).Handle,
            Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
    }

    /// <param name="targetDip">Target bounds in DIPs.</param>
    public void ShowAt(Rect targetDip, bool danger, TimeSpan? autoHide = null)
    {
        if (targetDip.IsEmpty || targetDip.Width <= 0 || targetDip.Height <= 0) return;
        Left = targetDip.Left - Pad;
        Top = targetDip.Top - Pad;
        Width = targetDip.Width + 2 * Pad;
        Height = targetDip.Height + 2 * Pad;
        _inner.Stroke = danger ? Palette.Danger : Palette.Pink;

        if (!IsVisible) Show();
        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(550))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        _root.BeginAnimation(OpacityProperty, pulse);

        _autoHide.Stop();
        if (autoHide is { } t)
        {
            _autoHide.Interval = t;
            _autoHide.Start();
        }
    }

    public void HideHighlight()
    {
        _autoHide.Stop();
        _root.BeginAnimation(OpacityProperty, null);
        if (IsVisible) Hide();
    }
}
