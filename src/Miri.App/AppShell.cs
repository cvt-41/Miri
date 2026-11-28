using System.IO;
using System.Windows;
using System.Windows.Threading;
using Miri.Core;
using Miri.Pet;
using WpfPoint = System.Windows.Point;

namespace Miri.App;

/// <summary>
/// The glue between the brain and the UI: implements <see cref="IMiriEvents"/> and <see cref="IApprovalGate"/>
/// (both may be called from background threads – everything is marshalled to the WPF dispatcher),
/// runs one brain task at a time on a background task and cancels it on the stop hotkey.
/// </summary>
internal sealed class AppShell : IMiriEvents, IApprovalGate
{
    private readonly Dispatcher _ui;
    private readonly MiriSettings _settings;
    private readonly PetController _pet;
    private readonly BubbleWindow _bubble;
    private readonly HighlightWindow _highlight;
    private readonly DispatcherTimer _idleMoodTimer;
    private IMiriBrain? _brain;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public AppShell(MiriSettings settings, PetController pet, BubbleWindow bubble, HighlightWindow highlight)
    {
        _ui = Dispatcher.CurrentDispatcher;
        _settings = settings;
        _pet = pet;
        _bubble = bubble;
        _pet.ClickThroughChanged += on =>   // the bubble can sit on the click target too
        {
            if (on) Win32.AddExStyle(_bubble.Handle, Win32.WS_EX_TRANSPARENT); else Win32.RemoveExStyle(_bubble.Handle, Win32.WS_EX_TRANSPARENT);
        };
        _highlight = highlight;

        _bubble.Submitted += OnSubmitted;
        _bubble.BubbleVisibilityChanged += visible => _pet.HoldStill = visible;
        _pet.PositionChanged += () => _bubble.Reposition(_pet.Bounds);
        _pet.Clicked += ToggleChat;
        _pet.Says += (text, fresh) =>
        {
            if (fresh && !_busy) _bubble.ClearReply();   // never wipe a running exchange   // each pinch starts a clean bubble, so the lines don't pile up
            ShowNote(text);
        };

        _idleMoodTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _idleMoodTimer.Tick += (_, _) =>
        {
            _idleMoodTimer.Stop();
            if (!_busy) _pet.SetMood(PetMood.Idle);
        };
    }

    public IMiriBrain? Brain => _brain;
    public bool IsBusy => _busy;
    /// <summary>The chat bubble (or an approval in it) is showing.</summary>
    public bool BubbleOpen => _bubble.IsVisible || _bubble.ApprovalPending;

    public void AttachBrain(IMiriBrain brain) => _brain = brain;

    public void ReportStartupError(string what, Exception ex)
    {
        Log($"{what}: {ex}");
        _bubble.ShowAbove(_pet.Bounds, focusInput: false);
        _bubble.AppendNote($"{what}: {ex.Message}");
        _pet.SetMood(PetMood.Confused);
        _idleMoodTimer.Start();
    }

    /// <summary>Shows an informational note in the bubble without stealing focus.</summary>
    public void ShowNote(string text)
    {
        _bubble.ShowAbove(_pet.Bounds, focusInput: false);
        _bubble.AppendNote(text);
    }

    // ------------------------------------------------------------------ chat

    public void OpenChat() => _bubble.ShowAbove(_pet.Bounds, focusInput: true);

    private void ToggleChat()
    {
        if (_bubble.IsVisible && !_bubble.ApprovalPending) _bubble.HideBubble();
        else OpenChat();
    }

    private async void OnSubmitted(string text)
    {
        if (_busy)
        {
            _bubble.SetStatus($"Still busy... ({_settings.StopHotkey} stops me)");
            return;
        }
        _bubble.BeginExchange(text);
        var brain = _brain;
        if (brain == null)
        {
            _bubble.AppendNote("My brain isn't connected, meow. (Check the error log in the data folder.)");
            _pet.SetMood(PetMood.Confused);
            _idleMoodTimer.Start();
            return;
        }

        _busy = true;
        _idleMoodTimer.Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _pet.SetMood(PetMood.Thinking);
        _bubble.SetStatus("thinking...");
        try
        {
            await Task.Run(() => brain.HandleAsync(text, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) _bubble.SetStatus("");
        }
        catch (OperationCanceledException)
        {
            // the stop hotkey already said so
        }
        catch (Exception ex)
        {
            Log("HandleAsync: " + ex);
            _bubble.AppendNote("Mrrp... something went wrong: " + ex.Message);
            _bubble.SetStatus("");
            _pet.SetMood(PetMood.Confused);
        }
        finally
        {
            _busy = false;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            _highlight.HideHighlight();
            _idleMoodTimer.Start();
        }
    }

    /// <summary>Stop hotkey: cancel whatever Miri is doing and say so.</summary>
    public void StopCurrent()
    {
        var cts = _cts;
        _bubble.ShowAbove(_pet.Bounds, focusInput: false);
        if (cts == null || !_busy)
        {
            _bubble.SetStatus("nothing to stop, meow");
            return;
        }
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        _highlight.HideHighlight();
        _bubble.AppendNote("Okay, I stopped. Paws off.");
        _bubble.SetStatus("stopped");
        _pet.SetMood(PetMood.Confused);
        _idleMoodTimer.Start();
    }

    // ------------------------------------------------------------------ IMiriEvents (any thread)

    public void OnToken(string text) => _ui.BeginInvoke(() =>
    {
        if (_cts?.IsCancellationRequested == true) return;
        _bubble.AppendToken(text);
        if (!_bubble.IsVisible) _bubble.ShowAbove(_pet.Bounds, focusInput: false);
    });

    public void OnMood(PetMood mood) => _ui.BeginInvoke(() =>
    {
        if (_cts?.IsCancellationRequested == true) return;
        _pet.SetMood(mood);
    });

    public void OnPointAt(System.Drawing.Rectangle screenBounds) => _ui.BeginInvoke(() => PointAt(screenBounds));

    /// <summary>Completes when she's in the reach pose, is picked up, or after 6 s.</summary>
    public Task PointAtAsync(System.Drawing.Rectangle screenBounds) => _ui.InvokeAsync(async () =>
    {
        var there = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Reached() => there.TrySetResult();
        void Moved() { if (_pet.IsDragging) there.TrySetResult(); }
        _pet.ReachedTarget += Reached;
        _pet.PositionChanged += Moved;
        try
        {
            if (!PointAt(screenBounds) || _pet.IsDragging) return;
            await Task.WhenAny(there.Task, Task.Delay(6000));
            _pet.PassClicksFor(TimeSpan.FromSeconds(2.5));   // the executor clicks right under her paw next
        }
        finally
        {
            _pet.ReachedTarget -= Reached;
            _pet.PositionChanged -= Moved;
        }
    }).Task.Unwrap();

    private bool PointAt(System.Drawing.Rectangle screenBounds)
    {
        var r = ScreenMetrics.ToDip(screenBounds);   // UIA bounds are physical pixels
        if (r.Width <= 0 || r.Height <= 0) return false;
        _pet.ReachFor(r);
        if (!_bubble.ApprovalPending) _highlight.ShowAt(r, danger: false, TimeSpan.FromSeconds(5));
        return true;
    }

    public void OnStatus(string text) => _ui.BeginInvoke(() => _bubble.SetStatus(text));

    // ------------------------------------------------------------------ IApprovalGate (any thread)

    public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<ApprovalDecision>(ct);
        return _ui.InvokeAsync(() => AskOnUiAsync(request, ct)).Task.Unwrap();
    }

    private async Task<ApprovalDecision> AskOnUiAsync(ApprovalRequest request, CancellationToken ct)
    {
        bool danger = request.Risk.Level == RiskLevel.Dangerous;
        if (request.TargetBounds is { } b && b.Width > 0 && b.Height > 0)
        {
            var r = ScreenMetrics.ToDip(b);
            _highlight.ShowAt(r, danger);
            _pet.WalkTo(new WpfPoint(r.Left + r.Width / 2, r.Top + r.Height / 2));
        }
        _pet.SetMood(PetMood.Pointing);
        _bubble.ShowAbove(_pet.Bounds, focusInput: true);
        _bubble.SetStatus(danger ? "needs a careful yes" : "may I?");
        try
        {
            var decision = await _bubble.AskApproval(request, ct);
            _bubble.SetStatus(decision == ApprovalDecision.No ? "okay, not doing that" : "on it");
            return decision;
        }
        finally
        {
            _highlight.HideHighlight();
            if (_busy) _pet.SetMood(PetMood.Thinking);
        }
    }

    // ------------------------------------------------------------------ misc

    public static void Log(string text)
    {
        try
        {
            File.AppendAllText(Path.Combine(MiriSettings.DataDir, "miri-errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { }
    }
}
