using System.Windows.Threading;
using Miri.Core;
using Miri.Hands;
using Miri.Pet;
using Drawing = System.Drawing;

namespace Miri.App;

/// <summary>
/// Every 15–40 minutes, when it's safe and you're watching, the kitten teases you: in a Chromium browser she paws
/// at the tab strip and Ctrl+Tabs to the next tab; elsewhere she paws at another app's taskbar button and brings it
/// forward. Then she looks smug. Never types text, clicks content, closes or minimizes anything, or opens the bubble.
/// </summary>
public sealed class Mischief
{
    public const uint MinIdleMs = 4000;               // not while you're typing / moving the mouse...
    public const uint MaxIdleMs = 10 * 60 * 1000;     // ...but you're there to see it

    private readonly MiriSettings _settings;
    private readonly PetController _pet;
    private readonly Func<bool> _uiBusy;
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private long _nextAtMs;
    private bool _teasing;

    /// <param name="uiBusy">True while the brain works or the chat bubble / an approval is showing.</param>
    public Mischief(MiriSettings settings, PetController pet, Func<bool> uiBusy)
    {
        _settings = settings;
        _pet = pet;
        _uiBusy = uiBusy;
        _nextAtMs = Environment.TickCount64 + Minutes(10, 25);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) =>
        {
            try { await TickAsync(); }
            catch (Exception ex) { AppShell.Log("Mischief: " + ex); }
        };
    }

    public void Start() => _timer.Start();

    /// <summary>The pure part of the safety gate (the foreground-window checks live in <see cref="Tease"/>).</summary>
    public static bool MayTease(bool enabled, long nowMs, long nextAtMs, bool petFree, bool uiBusy, uint idleMs, bool mouseDown) =>
        enabled && nowMs >= nextAtMs && petFree && !uiBusy && idleMs >= MinIdleMs && idleMs <= MaxIdleMs && !mouseDown;

    private bool PetFree =>
        !_pet.Paused && _pet.State is PetState.Sitting or PetState.Walking && _pet.Mood == PetMood.Idle &&
        _pet.CurrentAnimation is PetAnimation.Sit or PetAnimation.Walk;   // not purring, eating, sleepy-grumpy...

    private async Task TickAsync()
    {
        if (_teasing || !MayTease(_settings.Mischief, Environment.TickCount64, _nextAtMs, PetFree, _uiBusy(),
                                  PetController.IdleMilliseconds(), Tease.AnyMouseButtonDown()))
            return;
        if (Tease.Foreground(_ownPid) is not { } fg) return;   // not a normal app window / fullscreen / elevated: try later

        _teasing = true;
        try
        {
            long started = Environment.TickCount64;
            bool browser = Tease.IsChromium(fg.Process);
            Tease.Window? to = null;
            Drawing.Rectangle reach;
            if (browser)
            {
                reach = new Drawing.Rectangle(fg.Bounds.X + fg.Bounds.Width / 2 - 120, fg.Bounds.Y, 240, 40);   // tab strip
            }
            else
            {
                to = Tease.NextBelow(fg.Hwnd, _ownPid);
                if (to == null) { _nextAtMs = started + 60_000; return; }
                var target = to;
                var btn = await Task.Run(() => Tease.TaskbarButton(target));
                reach = btn.IsEmpty ? new Drawing.Rectangle(to.Bounds.X + to.Bounds.Width / 2 - 60, to.Bounds.Y, 120, 30) : btn;
            }
            _nextAtMs = started + Minutes(15, 40);   // one try per slot, whatever happens next

            if (!await ReachAsync(reach)) return;

            // nothing changed while she went over: still allowed, no input since we started, same window in front
            bool still = _settings.Mischief && !_pet.Paused && !_pet.IsDragging && !_uiBusy() && !Tease.AnyMouseButtonDown() &&
                         PetController.IdleMilliseconds() >= Environment.TickCount64 - started &&
                         Tease.Foreground(_ownPid)?.Hwnd == fg.Hwnd;
            if (!still) return;

            bool ok = browser ? Tease.NextTab(fg.Hwnd) : Tease.SwitchTo(to!.Hwnd);
            if (ok) _pet.Play(PetAnimation.Smug, 2.5);
            Tease.Log((browser
                ? $"Mischief: Ctrl+Tab in \"{fg.Title}\" ({fg.Process})"
                : $"Mischief: switched from {fg.Process} to \"{to!.Title}\" ({to.Process})") + (ok ? "" : " - didn't work"));
        }
        finally
        {
            _teasing = false;
        }
    }

    /// <summary>True when she reached it (or after 4 s); false if she was picked up.</summary>
    private async Task<bool> ReachAsync(Drawing.Rectangle physical)
    {
        var there = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Reached() => there.TrySetResult(true);
        void Moved() { if (_pet.IsDragging) there.TrySetResult(false); }
        _pet.ReachedTarget += Reached;
        _pet.PositionChanged += Moved;
        try
        {
            _pet.ReachFor(ScreenMetrics.ToDip(physical));
            return await Task.WhenAny(there.Task, Task.Delay(4000)) != there.Task || there.Task.Result;
        }
        finally
        {
            _pet.ReachedTarget -= Reached;
            _pet.PositionChanged -= Moved;
        }
    }

    private long Minutes(int lo, int hi) => (long)((lo + _rng.NextDouble() * (hi - lo)) * 60_000);
}
