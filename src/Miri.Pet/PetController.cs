using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Miri.Core;

namespace Miri.Pet;

/// <summary>
/// Public face of the kitten: runs <see cref="PetBehaviour"/> on a 30 fps DispatcherTimer,
/// plays the sprite animations and tells the host window where to be.
/// All coordinates are WPF DIPs in screen space.
/// </summary>
public sealed class PetController
{
    private readonly SpriteSheet _sheet;
    private readonly FloorTracker _floors = new();
    private readonly PetBehaviour _b = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _sw = new();
    private double _lastTick;
    private double _sinceFloorRefresh = 999;
    private Point _dragOffset;

    private PetAnimation _anim = PetAnimation.Sit;
    private bool _mirrored;
    private int _frameIndex;
    private double _frameElapsedMs;
    private Point _lastPos = new(double.NaN, double.NaN);

    public PetController(SpriteSheet sheet, int scale)
    {
        _sheet = sheet;
        PixelScale = Math.Clamp(scale, 1, 8);
        _b.Size = Size;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _timer.Tick += (_, _) => Tick();
        _b.ReachedTarget += () => ReachedTarget?.Invoke();
        ScreenMetrics.ScaleChanged += () =>
        {
            _b.Size = Size;
            SizeChanged?.Invoke();
        };
        CurrentFrame = sheet[PetAnimation.Sit].Frames[0];
    }

    // ------------------------------------------------------------------ state

    public SpriteSheet Sheet => _sheet;
    /// <summary>Physical screen pixels per sprite pixel.</summary>
    public int PixelScale { get; }
    /// <summary>Rendered sprite size in DIPs (keeps an exact integer physical scale).</summary>
    public double Size => _sheet.FrameWidth * PixelScale / ScreenMetrics.Scale;
    public SpriteFrame CurrentFrame { get; private set; }
    public PetAnimation CurrentAnimation => _anim;
    public PetState State => _b.State;
    public PetMood Mood => _b.Mood;
    /// <summary>Top-left of the sprite, DIPs.</summary>
    public Point Position => new(_b.X, _b.FeetY - Size);
    public Rect Bounds => new(_b.X, _b.FeetY - Size, Size, Size);
    public bool IsDragging => _b.State == PetState.Dragged;

    public bool Paused
    {
        get => _b.Paused;
        set => _b.Paused = value;
    }

    /// <summary>Don't wander off (e.g. while the chat bubble is open).</summary>
    public bool HoldStill
    {
        get => _b.HoldStill;
        set => _b.HoldStill = value;
    }

    public event Action? FrameChanged;
    public event Action? PositionChanged;
    public event Action? SizeChanged;
    public event Action? Clicked;
    /// <summary>She is in the reach pose for the last <see cref="ReachFor"/> (UI thread).</summary>
    public event Action? ReachedTarget;

    // ------------------------------------------------------------------ commands

    public void Start()
    {
        var work = ScreenMetrics.WorkAreaDip();
        _b.X = work.Right - Size - 220;
        _b.FeetY = work.Bottom - Size * 2;   // drop in from a little above the taskbar
        _sw.Start();
        _lastTick = 0;
        _timer.Start();
        Tick();
    }

    public void Stop() => _timer.Stop();

    public void SetMood(PetMood mood) => _b.SetMood(mood);

    /// <summary>Walk along the current floor towards a screen point (DIPs) and point at it.</summary>
    public void WalkTo(Point screenDip) => _b.WalkTo(screenDip);

    /// <summary>Walk to or jump onto a screen rect (DIPs) and tap it; <see cref="ReachedTarget"/> fires when she's there.</summary>
    public void ReachFor(Rect screenDip) =>
        _b.ReachFor(screenDip, _floors.WorkArea == default ? ScreenMetrics.WorkAreaDip() : _floors.WorkArea);

    /// <summary>Stroked by the mouse: purr for a few seconds.</summary>
    public void Pet() => _b.Pet();

    /// <summary>Eat from the bowl, then look happy; false = still full (head shake instead).</summary>
    public bool Feed() => _b.Feed();

    /// <summary>Show an animation for a few seconds while she sits (e.g. Smug after a tease).</summary>
    public void Play(PetAnimation anim, double seconds) => _b.Play(anim, seconds);

    /// <summary>Milliseconds since the last keyboard/mouse input anywhere in the session.</summary>
    public static uint IdleMilliseconds() => NativeMethods.IdleMilliseconds();

    public void BeginDrag(Point cursorDip)
    {
        _dragGeneration++;
        _floors.ClearLedge();   // picked up: forget the old ledge
        _dragOffset = new Point(cursorDip.X - _b.X, cursorDip.Y - _b.FeetY);
        _b.BeginDrag();
        Tick();
    }

    public void DragTo(Point cursorDip)
    {
        if (!IsDragging) return;
        _b.X = cursorDip.X - _dragOffset.X;
        _b.FeetY = cursorDip.Y - _dragOffset.Y;
        PublishPosition();
    }

    /// <summary>Finds ledges inside apps (video players...) under the drop point. Null = only title bars + taskbar.</summary>
    public ISurfaceFinder? SurfaceFinder { get; set; }

    private int _dragGeneration;

    public async void EndDrag()
    {
        int gen = ++_dragGeneration;
        var finder = SurfaceFinder;
        if (finder == null) { _b.EndDrag(); return; }

        // probe just below the paws (not on the sprite itself) for something to stand on
        double k = ScreenMetrics.Scale;
        var probe = new System.Drawing.Point((int)Math.Round((_b.X + Size / 2) * k), (int)Math.Round((_b.FeetY + 3) * k));
        int minWidth = (int)Math.Round(Size * 1.3 * k);   // a little wider than her
        ISurface? surface = null;
        try
        {
            var find = Task.Run(() => finder.FindAt(probe, minWidth));
            if (await Task.WhenAny(find, Task.Delay(900)) == find) surface = find.Result;
        }
        catch (Exception) { surface = null; }

        if (gen != _dragGeneration || !IsDragging) return;   // picked up again meanwhile
        if (surface != null)
        {
            _floors.SetLedge(surface);
            _floors.Refresh(Size);
            _sinceFloorRefresh = 0;
        }
        _b.EndDrag();
    }

    /// <summary>Called by the host when the kitten was clicked (not dragged).</summary>
    public void RaiseClicked()
    {
        _b.Poke();
        Clicked?.Invoke();
    }

    /// <summary>Is the sprite opaque at a point relative to the sprite's top-left (DIPs)?</summary>
    public bool HitTest(Point local)
    {
        double px = Size / CurrentFrame.Width;
        int x = (int)Math.Floor(local.X / px), y = (int)Math.Floor(local.Y / px);
        // be a little generous: accept a 1-sprite-pixel neighbourhood so the outline is easy to grab
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (CurrentFrame.IsOpaque(x + dx, y + dy)) return true;
        return false;
    }

    // ------------------------------------------------------------------ loop

    private void Tick()
    {
        double now = _sw.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastTick, 0, 0.1);
        _lastTick = now;

        _sinceFloorRefresh += dt;
        if (_sinceFloorRefresh >= (_floors.HasLedge ? 0.15 : 0.5))   // ledges move (scrolling): track them closely
        {
            _sinceFloorRefresh = 0;
            _floors.Refresh(Size);
        }

        uint idle = NativeMethods.IdleMilliseconds();
        var cursor = ScreenMetrics.CursorDip();
        var (anim, mirrored) = _b.Tick(dt, _floors.Floors, _floors.WorkArea == default ? ScreenMetrics.WorkAreaDip() : _floors.WorkArea, idle, cursor);

        Animate(anim, mirrored, dt);
        PublishPosition();
    }

    private void Animate(PetAnimation anim, bool mirrored, double dt)
    {
        var a = _sheet[anim];
        if (anim != _anim || mirrored != _mirrored)
        {
            _anim = anim;
            _mirrored = mirrored;
            _frameIndex = 0;
            _frameElapsedMs = 0;
        }
        else
        {
            _frameElapsedMs += dt * 1000;
            int guard = 0;
            while (_frameElapsedMs >= a.DurationsMs[_frameIndex] && guard++ < 16)
            {
                _frameElapsedMs -= a.DurationsMs[_frameIndex];
                _frameIndex = (_frameIndex + 1) % a.Frames.Count;
            }
        }
        var frame = (mirrored ? a.MirroredFrames : a.Frames)[_frameIndex];
        if (!ReferenceEquals(frame, CurrentFrame))
        {
            CurrentFrame = frame;
            FrameChanged?.Invoke();
        }
    }

    private void PublishPosition()
    {
        var p = Position;
        if (Math.Abs(p.X - _lastPos.X) < 0.01 && Math.Abs(p.Y - _lastPos.Y) < 0.01) return;
        _lastPos = p;
        PositionChanged?.Invoke();
    }
}
