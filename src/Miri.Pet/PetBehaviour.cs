using System.Windows;
using Miri.Core;

namespace Miri.Pet;

public enum PetState { Falling, Sitting, Walking, Sleeping, Dragged, WalkingTo, Pointing, Jumping }

/// <summary>
/// The kitten's state machine. Works in DIPs: <see cref="X"/> is the sprite's left edge,
/// <see cref="FeetY"/> the line its paws stand on. Pure logic – no WPF objects – driven by
/// <see cref="Tick"/> from <see cref="PetController"/>'s 30 fps timer.
/// </summary>
public sealed class PetBehaviour
{
    public const double Gravity = 2400;            // DIP/s²
    public const double WanderSpeed = 42;          // DIP/s
    public const double HurrySpeed = 120;
    public const double RunSpeed = 240;
    public const uint SleepAfterIdleMs = 2 * 60 * 1000;

    private readonly Random _rng = new();
    private double _clock;                // seconds since start
    private double _nextWanderAt = 4;
    private double _perkUntil;
    private double _pointUntil;
    private double _wakeGrumpUntil;
    private double _targetX;
    private int _facing = 1;              // +1 right, -1 left
    private Floor? _floor;
    private double _vy;
    private double _vx, _launchAt, _landX, _landY;   // ballistic jump (crouch until _launchAt)
    private double _squashUntil;                     // landing squash
    private PetAnimation _reach = PetAnimation.Point; // pose shown while State == Pointing
    private Floor? _perch;                           // temporary ledge: the top edge of what she's tapping
    private long _perchKey;
    private Rect? _pending;                          // target that arrived while airborne
    private bool _reachPending;
    private PetAnimation _act, _then;                // a short "real kitten" moment (purr, eat, smug...) that pauses wandering
    private double _actUntil, _thenFor;
    private double _lastFedAt = double.NegativeInfinity;

    /// <summary>Fed again within this many seconds she's still full.</summary>
    public const double FullForSeconds = 120;

    /// <summary>Raised from <see cref="Tick"/> once the reach pose for the last <see cref="ReachFor"/> is showing.</summary>
    public event Action? ReachedTarget;

    public double X { get; set; }
    public double FeetY { get; set; }
    public double Size { get; set; } = 96;
    public PetState State { get; private set; } = PetState.Falling;
    public PetMood Mood { get; private set; } = PetMood.Idle;
    public bool Paused { get; set; }
    /// <summary>Stay put (e.g. while the chat bubble is open).</summary>
    public bool HoldStill { get; set; }
    /// <summary>Carried by the cursor during play (looks happy, not startled).</summary>
    public bool Riding { get; set; }
    public Floor? CurrentFloor => _floor;
    public bool PointLeft { get; private set; }

    public double CenterX => X + Size / 2;

    // ------------------------------------------------------------------ commands

    public void SetMood(PetMood mood)
    {
        Mood = mood;
        if (mood != PetMood.Idle && State == PetState.Sleeping) State = PetState.Sitting;
        if (mood == PetMood.Pointing && State is PetState.Sitting or PetState.Walking)
        {
            State = PetState.Pointing;
            _reach = PetAnimation.Point;
            _pointUntil = _clock + 6;
        }
    }

    /// <summary>Walk along the current floor to stand under/next to <paramref name="target"/>, then point at it.</summary>
    public void WalkTo(Point target)
    {
        if (State is PetState.Dragged) return;
        if (State == PetState.Sleeping) State = PetState.Sitting;
        PointLeft = target.X < CenterX;
        _reach = PetAnimation.Point;
        // stand a little beside the target so the raised paw is towards it
        double standCenter = target.X + (PointLeft ? Size * 0.55 : -Size * 0.55);
        _targetX = standCenter - Size / 2;
        if (State == PetState.Falling) { _pointUntil = 0; return; }
        State = PetState.WalkingTo;
    }

    /// <summary>
    /// Go and touch <paramref name="target"/> (DIPs) like a cat: walk along her floor and reach down / sideways / up
    /// when it is within paw reach, else jump onto its top edge (a temporary ledge) and reach down onto it.
    /// Her sprite never covers the target's center (a real mouse click may land there).
    /// </summary>
    public void ReachFor(Rect target, Rect work)
    {
        if (State is PetState.Dragged || target.IsEmpty) return;
        _reachPending = true;
        if (State is PetState.Falling or PetState.Jumping) { _pending = target; return; }   // finish this flight first
        if (State == PetState.Sleeping) State = PetState.Sitting;

        double s = Size, cx = target.X + target.Width / 2, cy = target.Y + target.Height / 2;
        var f = _floor ?? new Floor(IntPtr.Zero, FeetY, work.Left, work.Right);
        double minX = Math.Max(f.Left - s * 0.35, work.Left), maxX = Math.Min(f.Right - s * 0.65, work.Right - s);   // where MoveTowards lets her stand
        bool left = cx < CenterX;
        // sprite X so a paw at `paw` (fraction of the width, facing right) is at cx; prefers facing the target
        (double X, bool Left)? Stand(double paw)
        {
            foreach (bool l in new[] { left, !left })
            {
                double x = l ? cx - (1 - paw) * s : cx - paw * s;
                if (x >= minX - 1 && x <= maxX + 1) return (x, l);
            }
            return null;
        }

        if (target.Top >= FeetY - 6 && target.Top <= FeetY + s * 0.6 && cy > FeetY && f.Covers(cx))
        {
            var st = Stand(0.8) ?? (Math.Clamp(left ? cx - 0.2 * s : cx - 0.8 * s, minX, Math.Max(minX, maxX)), left);
            WalkReach(st.X, st.Left, PetAnimation.ReachDown);
            return;
        }
        if (cy >= FeetY - s && cy <= FeetY && Stand(1.05) is { } side)          // body height: stand beside, paw sideways
        {
            WalkReach(side.X, side.Left, PetAnimation.Point);
            return;
        }
        if (cy < FeetY - s && target.Bottom >= FeetY - s * 1.4 && Stand(0.8) is { } under)   // just over her head
        {
            WalkReach(under.X, under.Left, PetAnimation.ReachUp);
            return;
        }

        // jump onto the target's top edge, reaching paw above its center
        JumpOnto(target, work, left ? cx - 0.2 * s : cx - 0.8 * s, left);
    }

    /// <summary>Jump onto <paramref name="target"/>'s top edge standing just left of its center, facing it (her paw is at its center).</summary>
    public void JumpLeftOf(Rect target, Rect work)
    {
        if (State is PetState.Dragged || target.IsEmpty) return;
        if (State == PetState.Sleeping) State = PetState.Sitting;
        _reachPending = true;
        JumpOnto(target, work, target.X + target.Width / 2 - 0.85 * Size, false);
    }

    /// <summary>While standing where she jumped (Pointing), hold <paramref name="anim"/> for <paramref name="seconds"/>, then drop down as usual.</summary>
    public void HoldPose(PetAnimation anim, double seconds, bool faceLeft = false)
    {
        if (State != PetState.Pointing) return;
        _reach = anim;
        PointLeft = faceLeft;
        _pointUntil = _clock + seconds;
    }

    private void JumpOnto(Rect target, Rect work, double x, bool left)
    {
        double s = Size;
        double x1 = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - s));
        double land = target.Top;
        _perch = new Floor(IntPtr.Zero, land, Math.Min(target.Left, x1 + s * 0.35), Math.Max(target.Right, x1 + s * 0.65), --_perchKey);
        double apex = Math.Min(FeetY, land) - Math.Max(40, 0.25 * Math.Abs(x1 - X));
        apex = Math.Min(Math.Max(apex, work.Top + s), Math.Min(FeetY, land) - 8);   // keep her on screen at the top of the arc
        _vy = -Math.Sqrt(2 * Gravity * (FeetY - apex));
        double flight = -_vy / Gravity + Math.Sqrt(2 * (land - apex) / Gravity);
        _vx = (x1 - X) / flight;
        _landX = x1;
        _landY = land;
        _launchAt = _clock + 0.15;   // crouch first
        PointLeft = left;
        _facing = left ? -1 : 1;
        _reach = PetAnimation.ReachDown;
        State = PetState.Jumping;
    }

    private void WalkReach(double x, bool left, PetAnimation reach)
    {
        PointLeft = left;
        _reach = reach;
        _targetX = x;
        _pointUntil = _clock + 6;
        State = Math.Abs(x - X) < 1.5 ? PetState.Pointing : PetState.WalkingTo;
    }

    public void BeginDrag()
    {
        State = PetState.Dragged;
        _floor = null;
        _perch = null;
        _pending = null;
        _reachPending = false;
        _vy = 0;
    }

    public void EndDrag()
    {
        if (State != PetState.Dragged) return;
        State = PetState.Falling;
        _vy = 0;
    }

    public void Poke()
    {
        if (State == PetState.Sleeping)
        {
            State = PetState.Sitting;
            _wakeGrumpUntil = _clock + 1.2;
        }
        _nextWanderAt = _clock + 8;
    }

    /// <summary>Show <paramref name="anim"/> for a few seconds while sitting (then <paramref name="then"/>, if given).</summary>
    public void Play(PetAnimation anim, double seconds, PetAnimation? then = null, double thenSeconds = 0)
    {
        if (State == PetState.Sleeping) State = PetState.Sitting;
        if (State == PetState.Pointing && Mood != PetMood.Pointing) _pointUntil = _clock;   // end a finished reach pose
        _wakeGrumpUntil = 0;
        if (_act != anim || _clock >= _actUntil) _act = anim;
        _actUntil = _clock + seconds;
        _then = then ?? anim;
        _thenFor = then == null ? 0 : thenSeconds;
        _nextWanderAt = Math.Max(_nextWanderAt, _actUntil + _thenFor + 4);
    }

    /// <summary>Stroked: purr (re-arms while the stroking goes on). Wakes her happily, never grumpy. Doesn't interrupt a meal.</summary>
    public void Pet()
    {
        if (State == PetState.Dragged || (_act == PetAnimation.Eat && _clock < _actUntil)) return;
        Play(PetAnimation.Purr, 3);
    }

    /// <summary>Eat, then look happy. Returns false (and shakes her head) when she was fed less than <see cref="FullForSeconds"/> ago.</summary>
    public bool Feed()
    {
        if (_clock - _lastFedAt < FullForSeconds)
        {
            Play(PetAnimation.Confused, 1.5);
            return false;
        }
        _lastFedAt = _clock;
        Play(PetAnimation.Eat, 4.5, PetAnimation.Happy, 2);
        return true;
    }

    // ------------------------------------------------------------------ tick

    /// <summary>Advances the simulation and returns the animation to show.</summary>
    public (PetAnimation Anim, bool Mirrored) Tick(double dt, IReadOnlyList<Floor> floors, Rect work,
                                                    uint idleMs, Point cursor)
    {
        var shown = TickCore(dt, floors, work, idleMs, cursor);
        if (_reachPending && State == PetState.Pointing && shown.Anim == _reach)
        {
            _reachPending = false;
            ReachedTarget?.Invoke();
        }
        return shown;
    }

    private (PetAnimation Anim, bool Mirrored) TickCore(double dt, IReadOnlyList<Floor> floors, Rect work,
                                                         uint idleMs, Point cursor)
    {
        _clock += dt;

        if (State == PetState.Dragged)
            return (Riding ? PetAnimation.Happy : PetAnimation.Confused, false);

        if (State == PetState.Jumping)
        {
            if (_clock < _launchAt) return (PetAnimation.Crouch, PointLeft);
            _floor = null;
            // exact constant-gravity step, so the apex and landing match the plan at any frame rate
            double vy = _vy + Gravity * dt, next = FeetY + _vy * dt + 0.5 * Gravity * dt * dt;
            if (vy <= 0 || next < _landY)
            {
                X += _vx * dt;
                FeetY = next;
                _vy = vy;
                return (vy < 0 ? PetAnimation.Jump : PetAnimation.Fall, _vx < 0);
            }
            X = _landX;
            FeetY = _landY;
            _vy = 0;
            _floor = _perch;
            State = PetState.Pointing;
            _pointUntil = _clock + 0.12 + 6;
            _squashUntil = _clock + 0.12;
            return (PetAnimation.Crouch, PointLeft);
        }

        // keep inside the work area horizontally
        X = Math.Clamp(X, work.Left, Math.Max(work.Left, work.Right - Size));

        if (State != PetState.Falling)
            FollowFloor(floors);

        if (State == PetState.Falling)
            return Fall(dt, floors, work);

        if (_clock < _squashUntil) return (PetAnimation.Crouch, _facing < 0);
        if (_pending is { } p)
        {
            _pending = null;
            ReachFor(p, work);
            if (State == PetState.Jumping) return (PetAnimation.Crouch, PointLeft);
        }

        // ---- sleep / wake
        if (State == PetState.Sleeping)
        {
            if (idleMs < 1500)
            {
                State = PetState.Sitting;
                _wakeGrumpUntil = _clock + 1.2;
            }
            else return (PetAnimation.Sleep, false);
        }
        if (!Paused && Mood == PetMood.Idle && idleMs > SleepAfterIdleMs &&
            State is PetState.Sitting or PetState.Walking)
        {
            State = PetState.Sleeping;
            return (PetAnimation.Sleep, false);
        }

        // ---- walking to a point target (brain pointing)
        if (State == PetState.WalkingTo)
        {
            double speed = Math.Abs(_targetX - X) > 400 ? RunSpeed : HurrySpeed;
            if (MoveTowards(_targetX, speed, dt))
            {
                State = PetState.Pointing;
                _pointUntil = _clock + 6;
            }
            return (PetAnimation.Walk, _facing < 0);
        }
        if (State == PetState.Pointing)
        {
            if (_clock > _pointUntil && Mood != PetMood.Pointing)
            {
                if (_perch is { } perch && _floor == perch)
                {
                    // done tapping: the ledge goes away and she drops to whatever is below
                    _perch = null;
                    _floor = null;
                    _vy = 0;
                    State = PetState.Falling;
                    return Fall(dt, floors, work);
                }
                State = PetState.Sitting;
                _nextWanderAt = _clock + 6;
            }
            else return (_reach, PointLeft);
        }

        // ---- a short kitten moment (petted, eating, smug) beats moods and wandering
        if (_clock >= _actUntil && _thenFor > 0)
        {
            _act = _then;
            _actUntil = _clock + _thenFor;
            _thenFor = 0;
        }
        if (_clock < _actUntil && State is PetState.Sitting or PetState.Walking)
        {
            State = PetState.Sitting;
            return (_act, false);
        }

        // ---- moods from the brain win over idle behaviour
        if (Mood != PetMood.Idle)
        {
            if (State == PetState.Walking) State = PetState.Sitting;
            return (Mood switch
            {
                PetMood.Thinking => PetAnimation.Think,
                PetMood.Talking => PetAnimation.Talk,
                PetMood.Happy => PetAnimation.Happy,
                PetMood.Confused => PetAnimation.Confused,
                PetMood.Pointing => PetAnimation.Point,
                _ => PetAnimation.Sit,
            }, Mood == PetMood.Pointing && PointLeft);
        }

        if (_clock < _wakeGrumpUntil)
            return (PetAnimation.Confused, false);

        // ---- deaf-cat gag: no reaction to sounds at all, but perks up at nearby input
        if (State == PetState.Sitting && idleMs < 250)
        {
            double dx = cursor.X - CenterX, dy = cursor.Y - (FeetY - Size / 2);
            if (dx * dx + dy * dy < (Size * 1.8) * (Size * 1.8))
                _perkUntil = _clock + 1.2;
        }

        // ---- wander
        if (State == PetState.Walking)
        {
            if (Paused || HoldStill) State = PetState.Sitting;
            else if (MoveTowards(_targetX, WanderSpeed, dt))
            {
                State = PetState.Sitting;
                _nextWanderAt = _clock + 5 + _rng.NextDouble() * 12;
            }
            else return (PetAnimation.Walk, _facing < 0);
        }

        if (State == PetState.Sitting)
        {
            if (!Paused && !HoldStill && _clock >= _nextWanderAt && _clock >= _perkUntil)
                StartWander(work);
            if (State == PetState.Walking) return (PetAnimation.Walk, _facing < 0);
        }

        if (_clock < _perkUntil) return (PetAnimation.Think, false);
        return (PetAnimation.Sit, false);
    }

    private void StartWander(Rect work)
    {
        var f = _floor ?? new Floor(IntPtr.Zero, FeetY, work.Left, work.Right);
        double minX = Math.Max(f.Left - Size * 0.3, work.Left);
        double maxX = Math.Min(f.Right - Size * 0.7, work.Right - Size);
        if (maxX - minX < 30) { _nextWanderAt = _clock + 8; return; }

        for (int i = 0; i < 6; i++)
        {
            double t = minX + _rng.NextDouble() * (maxX - minX);
            // short strolls mostly, the occasional long walk
            if (Math.Abs(t - X) < 40) continue;
            if (Math.Abs(t - X) > 420 && _rng.NextDouble() < 0.7) continue;
            _targetX = t;
            State = PetState.Walking;
            return;
        }
        _nextWanderAt = _clock + 3;
    }

    /// <returns>true when arrived (or blocked by the end of the floor).</returns>
    private bool MoveTowards(double targetX, double speed, double dt)
    {
        if (_floor is { } f)
        {
            // don't walk off the edge of a title bar while strolling / pointing
            double minX = f.Left - Size * 0.35, maxX = f.Right - Size * 0.65;
            targetX = Math.Clamp(targetX, minX, Math.Max(minX, maxX));
        }
        double d = targetX - X;
        if (Math.Abs(d) < 1.5) { X = targetX; return true; }
        _facing = d > 0 ? 1 : -1;
        double step = speed * dt;
        if (step >= Math.Abs(d)) { X = targetX; return true; }
        X += _facing * step;
        return false;
    }

    private void FollowFloor(IReadOnlyList<Floor> floors)
    {
        if (_perch is { } perch && _floor == perch) return;   // standing on what she's tapping
        double cx = CenterX;
        // 1) ride the window we're standing on (it may have moved)
        if (_floor is { } cur && cur.Hwnd != IntPtr.Zero)
        {
            Floor? best = null;
            foreach (var f in floors)
            {
                if (f.Hwnd != cur.Hwnd || f.Key != cur.Key) continue;
                // same window, same span width => moved: carry the kitten along
                if (Math.Abs((f.Right - f.Left) - (cur.Right - cur.Left)) < 2 && Math.Abs(f.Y - cur.Y) < 400)
                {
                    X += f.Left - cur.Left;
                    cx = CenterX;
                }
                if (f.Covers(cx)) { best = f; break; }
            }
            if (best is { } b)
            {
                _floor = b;
                FeetY = b.Y;
                return;
            }
            if (cur.IsLedge)
            {
                // the ledge resized (theater / fullscreen) but still exists: stay on it, inside its new span
                foreach (var f in floors)
                {
                    if (f.Hwnd != cur.Hwnd || f.Key != cur.Key) continue;
                    X = Math.Clamp(X, f.Left - Size * 0.3, Math.Max(f.Left - Size * 0.3, f.Right - Size * 0.7));
                    _floor = f;
                    FeetY = f.Y;
                    return;
                }
            }
        }
        // 2) any floor right under the paws
        foreach (var f in floors)
        {
            if (f.Covers(cx) && Math.Abs(f.Y - FeetY) <= 2)
            {
                _floor = f;
                FeetY = f.Y;
                return;
            }
        }
        // 3) nothing: fall
        _floor = null;
        _vy = 0;
        State = PetState.Falling;
    }

    private (PetAnimation, bool) Fall(double dt, IReadOnlyList<Floor> floors, Rect work)
    {
        _vy = Math.Min(_vy + Gravity * dt, 2400);
        double next = FeetY + _vy * dt;
        double cx = CenterX;
        Floor? land = null;
        foreach (var f in floors)
        {
            if (!f.Covers(cx)) continue;
            if (f.Y >= FeetY - 0.5 && f.Y <= next && (land == null || f.Y < land.Value.Y))
                land = f;
        }
        if (land == null && next >= work.Bottom)
            land = new Floor(IntPtr.Zero, work.Bottom, work.Left, work.Right);

        if (land is { } l)
        {
            FeetY = l.Y;
            _floor = l;
            _vy = 0;
            if (State == PetState.Falling)
            {
                if (_targetX != 0 && _pointUntil == 0 && Mood == PetMood.Pointing) State = PetState.WalkingTo;
                else State = PetState.Sitting;
            }
            _nextWanderAt = _clock + 3 + _rng.NextDouble() * 5;
            _squashUntil = _clock + 0.12;
            return (PetAnimation.Crouch, _facing < 0);
        }

        FeetY = next;
        return (PetAnimation.Fall, _facing < 0);
    }
}
