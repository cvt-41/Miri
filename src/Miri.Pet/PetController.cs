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
        _b.ReachedTarget += () =>
        {
            ReachedTarget?.Invoke();
            if (Playing && (ScreenMetrics.CursorDip() - new Point(_b.CenterX, _b.FeetY - Size / 2)).Length < Size * 1.3)
            {
                _b.Play(PetAnimation.Tug, 0.9);
                _tugTicks = 8;
            }
        };
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
    /// <summary>True: let mouse clicks pass through her (she is standing on what Miri is about to click).</summary>
    public event Action<bool>? ClickThroughChanged;
    private int _clickThroughGen;

    /// <summary>Clicks go through the kitten for <paramref name="time"/>, so a real mouse click under her paw reaches the app.</summary>
    public async void PassClicksFor(TimeSpan time)
    {
        int gen = ++_clickThroughGen;
        ClickThroughChanged?.Invoke(true);
        await Task.Delay(time);
        if (gen == _clickThroughGen) ClickThroughChanged?.Invoke(false);
    }

    // ------------------------------------------------------------------ commands

    public void Start()
    {
        var work = ScreenMetrics.WorkAreaDip();
        _b.X = work.Right - Size - 220;
        _b.FeetY = work.Bottom;               // on the taskbar's top edge...
        _b.Play(PetAnimation.Climb, 1.25);    // ...climbing up from behind it (drawn in place, ears first)
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

    /// <summary>Jump onto a target's top edge just left of its center (DIPs), e.g. a progress-bar dot.</summary>
    public void JumpLeftOf(Rect screenDip) =>
        _b.JumpLeftOf(screenDip, _floors.WorkArea == default ? ScreenMetrics.WorkAreaDip() : _floors.WorkArea);

    /// <summary>Hold a pose where she landed for a while, then drop down.</summary>
    public void HoldPose(PetAnimation anim, double seconds) => _b.HoldPose(anim, seconds);

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
        if (Playing) { StopPlay(); return; }   // a click ends the game
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

    // ------------------------------------------------------------------ play (cursor as a toy)

    private double _playUntil, _stillFor, _nextChase, _rideUntil, _nextSad;
    private int _tugTicks;
    private bool _sad, _probing;
    private Point _lastCursor, _chased = new(double.NaN, double.NaN);

    public bool Playing => _playUntil > _sw.Elapsed.TotalSeconds;
    public bool Riding => _b.Riding;

    /// <summary>She chases the cursor, pounces and tugs it; hold the cursor still just above her head and she hops on for a ride.</summary>
    public void StartPlay(double seconds = 90) { _playUntil = _sw.Elapsed.TotalSeconds + seconds; _b.SetMood(PetMood.Happy); }

    public void StopPlay()
    {
        _playUntil = 0;
        _sad = false;
        _nudgeAt = 0;
        if (NativeMethods.PointerHidden) NativeMethods.ShowPointer();
        _tugTicks = 0;
        if (_b.Riding) Dismount();
        _b.SetMood(PetMood.Idle);
    }

    /// <summary>Brings the real pointer back (call at start and exit: a crash mid-hug must not leave it hidden).</summary>
    public static void RestorePointer() => NativeMethods.ShowPointer();

    /// <summary>The cursor is "offered" when it rests just above her head.</summary>
    public static bool IsOffer(Point cursor, Rect body) =>
        Math.Abs(cursor.X - (body.Left + body.Width / 2)) < body.Width * 0.35 &&
        cursor.Y > body.Top - body.Height * 0.5 && cursor.Y < body.Top + body.Height * 0.15;

    private void PlayStep(double dt, double now, Point cursor, uint idle)
    {
        double speed = dt > 0 ? (cursor - _lastCursor).Length / dt : 0;
        _lastCursor = cursor;
        if (idle > 20000 && !_b.Riding) { StopPlay(); return; }   // you wandered off

        if (_b.Riding)
        {
            if (speed > 3000 || now > _rideUntil || now > _playUntil) { Dismount(); return; }   // shaken off, or bored
            DragTo(cursor);
            return;
        }

        _stillFor = speed < 60 ? _stillFor + dt : 0;
        if (_stillFor > 0.8 && IsOffer(cursor, Bounds) && _b.State is not (PetState.Jumping or PetState.Falling))
        {
            // hop on: feet on the pointer tip, clicks pass through her while she rides
            _b.X = cursor.X - Size / 2;
            _b.FeetY = cursor.Y;
            BeginDrag(cursor);
            _b.Riding = true;
            _rideUntil = now + 20;
            ClickThroughChanged?.Invoke(true);
            return;
        }

        if (_tugTicks > 0)
        {
            // caught it: reel the pointer in to her center, from whatever side it is on
            _tugTicks--;
            var to = Center(Bounds) - cursor;
            if (to.Length <= Size * HugReach) { _tugTicks = 0; return; }
            to *= Math.Min(6, to.Length - Size * HugReach) / to.Length;
            double k = ScreenMetrics.Scale;
            NativeMethods.SetCursorPos((int)Math.Round((cursor.X + to.X) * k), (int)Math.Round((cursor.Y + to.Y) * k));
            return;
        }

        // pointer close on any side (above, beside, below): hug it and nudge it, right where she is
        if (now >= _nextHug && IsNear(cursor, Bounds, Size) && !IsOffer(cursor, Bounds) && _b.State is not (PetState.Jumping or PetState.Falling or PetState.Dragged))
        {
            _sad = false;
            _b.Play(PetAnimation.Hug, 1.4, PetAnimation.Nudge, 1.2);
            NativeMethods.HidePointer();   // she holds it in her arms now (drawn in the Hug frames)
            _nudgeAt = now + 1.4;
            _nextHug = now + 4;
            _nextChase = now + 3;
            return;
        }
        // no ledge near the pointer: she waits, sad, until it comes to her
        if (_sad && now >= _nextSad) { _b.Play(PetAnimation.Sad, 2); _nextSad = now + 1.8; }
        if (_nudgeAt > 0 && now >= _nudgeAt)
        {
            // hug over, head-bump: the pointer comes back and gets pushed gently away
            _nudgeAt = 0;
            NativeMethods.ShowPointer();
            double k = ScreenMetrics.Scale, dir = cursor.X >= _b.CenterX ? 1 : -1;
            NativeMethods.SetCursorPos((int)Math.Round((cursor.X + dir * 10) * k), (int)Math.Round(cursor.Y * k));
        }

        if (now < _nextChase || _probing || _b.State is PetState.Jumping or PetState.Falling or PetState.Dragged) return;
        if (!double.IsNaN(_chased.X) && (cursor - _chased).Length < Size * 0.4 && speed < 60) return;   // already went for it
        _chased = cursor;
        _nextChase = now + 0.7;
        GoForPointer(cursor);
    }

    private double _nudgeAt, _nextHug;
    private bool _faceLeft;
    private const double HugReach = 0.15;   // hug only with the pointer at her center (Hug frames draw it in her arms there)

    private static Point Center(Rect body) => new(body.Left + body.Width / 2, body.Top + body.Height / 2);

    /// <summary>Is the pointer at the center of her body, where the Hug frames hold it?</summary>
    public static bool IsNear(Point cursor, Rect body, double size) => (cursor - Center(body)).Length <= size * HugReach;

    /// <summary>Highest edge a cat could stand on to reach a pointer at <paramref name="cursor"/>: just below it, not far below.</summary>
    public static double? EdgeUnder(Point cursor, IEnumerable<Floor> floors, double size) =>
        floors.Where(f => f.Left <= cursor.X && cursor.X <= f.Right && f.Y >= cursor.Y - 4 && f.Y <= cursor.Y + size * 1.2)
              .Select(f => (double?)f.Y).OrderBy(y => y).FirstOrDefault();

    /// <summary>Jump only onto real things: title bars, the taskbar, or an app's own UI (a chat bar, a panel) under the pointer.</summary>
    private async void GoForPointer(Point cursor)
    {
        if (EdgeUnder(cursor, _floors.Floors, Size) is { } y) { Pounce(cursor, y); return; }

        var finder = SurfaceFinder;
        if (finder == null) { BeSad(); return; }
        _probing = true;
        ISurface? surface = null;
        try
        {
            double k = ScreenMetrics.Scale;
            var p = new System.Drawing.Point((int)Math.Round(cursor.X * k), (int)Math.Round(cursor.Y * k));
            var find = Task.Run(() => finder.FindAt(p, (int)Math.Round(Size * 1.3 * k)));
            if (await Task.WhenAny(find, Task.Delay(900)) == find) surface = find.Result;
        }
        catch (Exception) { surface = null; }
        finally { _probing = false; }
        if (!Playing || _b.Riding) return;

        if (surface?.CurrentBounds() is { } r)
        {
            double k = ScreenMetrics.Scale, edge = (surface.TopEdge ? r.Top : r.Bottom) / k;
            if (edge >= cursor.Y - 4 && edge <= cursor.Y + Size * 1.2)
            {
                _floors.SetLedge(surface);     // a real ledge: she can stand there as long as it is on screen
                _floors.Refresh(Size);
                Pounce(cursor, edge);
                return;
            }
        }
        BeSad();
    }

    private void Pounce(Point cursor, double edgeY)
    {
        _sad = false;
        ReachFor(new Rect(cursor.X - 3, edgeY - 2, 6, 4));   // land on that edge right under the pointer (same physics as tapping buttons)
    }

    private void BeSad()
    {
        if (_sad) return;
        _sad = true;
        _nextSad = 0;
    }

    private void Dismount()
    {
        _b.Riding = false;
        ClickThroughChanged?.Invoke(false);
        EndDrag();   // falls with gravity onto whatever is below
        _stillFor = 0;
        _chased = new Point(double.NaN, double.NaN);
        _nextChase = _sw.Elapsed.TotalSeconds + 1.5;
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
        if (Playing) PlayStep(dt, now, cursor, idle);
        var (anim, mirrored) = _b.Tick(dt, _floors.Floors, _floors.WorkArea == default ? ScreenMetrics.WorkAreaDip() : _floors.WorkArea, idle, cursor);
        if (Playing && !_b.Riding && anim is not (PetAnimation.Walk or PetAnimation.Jump or PetAnimation.Fall or PetAnimation.Crouch))
        {
            // face the pointer (frames face right); dead zone at her middle so a hug doesn't flicker left/right
            double dx = cursor.X - _b.CenterX;
            if (Math.Abs(dx) > Size * HugReach) _faceLeft = dx < 0;
            mirrored = _faceLeft;
        }

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
