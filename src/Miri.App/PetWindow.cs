using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Miri.Pet;
using WpfPoint = System.Windows.Point;

namespace Miri.App;

/// <summary>
/// Transparent, topmost, tool-window host for the kitten. Clicks on transparent pixels fall through
/// (layered alpha + WM_NCHITTEST → HTTRANSPARENT). Left-drag picks the kitten up; a click raises Clicked;
/// stroking the cursor back and forth over her (no button) pets her.
/// </summary>
public sealed class PetWindow : Window
{
    private readonly PetController _pet;
    private readonly PetView _view;
    private readonly DispatcherTimer _topmostTimer;
    private bool _mouseDown;
    private bool _dragging;
    private WpfPoint _downCursor;
    private readonly StrokeDetector _strokes = new();

    public PetWindow(PetController pet)
    {
        _pet = pet;
        _view = new PetView(pet);

        Title = "Miri";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        Content = _view;
        Left = -10000; Top = -10000;   // until the controller places us

        _pet.PositionChanged += OnPetMoved;
        _pet.ClickThroughChanged += on =>
        {
            if (Handle == IntPtr.Zero) return;
            if (on) Win32.AddExStyle(Handle, Win32.WS_EX_TRANSPARENT); else Win32.RemoveExStyle(Handle, Win32.WS_EX_TRANSPARENT);
        };

        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) => KeepOnTop();
    }

    public IntPtr Handle { get; private set; }
    public PetController Pet => _pet;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        Win32.AddExStyle(Handle, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE);
        HwndSource.FromHwnd(Handle)?.AddHook(WndProc);
        ScreenMetrics.UpdateFrom(this);
        _topmostTimer.Start();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScreenMetrics.SetScale(newDpi.DpiScaleX);
    }

    private void OnPetMoved()
    {
        var p = _pet.Position;
        Left = p.X;
        Top = p.Y;
    }

    private void KeepOnTop()
    {
        if (Handle != IntPtr.Zero)
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32.WM_NCHITTEST:
            {
                long lp = lParam.ToInt64();
                int x = unchecked((short)(lp & 0xFFFF));
                int y = unchecked((short)((lp >> 16) & 0xFFFF));
                try
                {
                    var local = PointFromScreen(new WpfPoint(x, y));
                    if (!_pet.HitTest(local))
                    {
                        handled = true;
                        return new IntPtr(Win32.HTTRANSPARENT);
                    }
                }
                catch (InvalidOperationException) { }
                break;
            }
            case Win32.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(Win32.MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ mouse

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _strokes.Reset();
        _mouseDown = true;
        _dragging = false;
        _downCursor = ScreenMetrics.CursorDip();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var c = ScreenMetrics.CursorDip();
        if (!_mouseDown)
        {
            // only arrives over her opaque pixels (WM_NCHITTEST), so this is the hand on her fur
            if (e.LeftButton == MouseButtonState.Released && e.RightButton == MouseButtonState.Released &&
                _strokes.Add(Environment.TickCount64 / 1000.0, c.X, c.Y))
                _pet.Pet();
            return;
        }
        if (!_dragging && (Math.Abs(c.X - _downCursor.X) > 4 || Math.Abs(c.Y - _downCursor.Y) > 4))
        {
            _dragging = true;
            _pet.BeginDrag(_downCursor);
        }
        if (_dragging) _pet.DragTo(c);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_mouseDown) return;
        _mouseDown = false;
        bool wasDragging = _dragging;   // ReleaseMouseCapture runs OnLostMouseCapture, which resets _dragging
        _dragging = false;
        ReleaseMouseCapture();
        if (wasDragging) _pet.EndDrag();
        else _pet.RaiseClicked();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) _pet.EndDrag();
        _mouseDown = _dragging = false;
    }
}
