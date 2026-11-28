using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using Miri.Core;
using Miri.Pet;

namespace Miri.App;

/// <summary>Pixel speech bubble above (or, near the top of the screen, below) the kitten: input, streaming reply, status line, approval card.</summary>
public partial class BubbleWindow : Window
{
    private Run? _replyRun;
    private TaskCompletionSource<ApprovalDecision>? _approval;
    private CancellationTokenRegistration _approvalReg;
    private Rect _anchor;           // kitten bounds (DIPs)
    private bool _below;            // bubble under her paws, tail pointing up

    public BubbleWindow()
    {
        InitializeComponent();
        Input.KeyDown += OnInputKeyDown;
        Input.TextChanged += (_, _) => Placeholder.Visibility = Input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.Click += (_, _) => HideBubble();
        YesButton.Click += (_, _) => Answer(ApprovalDecision.Yes);
        NoButton.Click += (_, _) => Answer(ApprovalDecision.No);
        YesAllButton.Click += (_, _) => Answer(ApprovalDecision.YesToAllForTask);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { HideBubble(); e.Handled = true; }
        };
        SizeChanged += (_, _) => Reposition();

        // Screen readers: announce the reply once streaming pauses, not on every token.
        _announceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _announceTimer.Tick += (_, _) =>
        {
            _announceTimer.Stop();
            var peer = UIElementAutomationPeer.FromElement(ReplyText) ?? UIElementAutomationPeer.CreatePeerForElement(ReplyText);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
    }

    private readonly DispatcherTimer _announceTimer;

    /// <summary>Mirrors the reply text into AutomationProperties.Name (TextBlock Inlines aren't exposed to UIA otherwise).</summary>
    private void UpdateReplyAutomation()
    {
        var text = new TextRange(ReplyText.ContentStart, ReplyText.ContentEnd).Text.Trim();
        AutomationProperties.SetName(ReplyText, text);
        _announceTimer.Stop();
        if (text.Length > 0) _announceTimer.Start();
    }

    /// <summary>User pressed Enter with some text.</summary>
    public event Action<string>? Submitted;
    /// <summary>Visibility changed (shown / hidden by the user).</summary>
    public event Action<bool>? BubbleVisibilityChanged;

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();
    public bool AllowClose { get; set; }
    public bool ApprovalPending => _approval != null;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Win32.AddExStyle(new WindowInteropHelper(this).Handle, Win32.WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideBubble();
        }
        base.OnClosing(e);
    }

    // ------------------------------------------------------------------ show / place

    public void ShowAbove(Rect kittenBounds, bool focusInput)
    {
        _anchor = kittenBounds;
        bool wasVisible = IsVisible;
        if (!wasVisible)
        {
            Show();
            BubbleVisibilityChanged?.Invoke(true);
        }
        Reposition();
        if (focusInput)
        {
            Activate();
            Win32.SetForegroundWindow(Handle);
            if (!ApprovalPending)
            {
                Input.Focus();
                Keyboard.Focus(Input);
            }
        }
    }

    public void HideBubble()
    {
        if (ApprovalPending) Answer(ApprovalDecision.No);   // closing the bubble never approves anything
        if (!IsVisible) return;
        Hide();
        BubbleVisibilityChanged?.Invoke(false);
    }

    /// <summary>Follow the kitten.</summary>
    public void Reposition(Rect kittenBounds)
    {
        _anchor = kittenBounds;
        if (IsVisible) Reposition();
    }

    private void Reposition()
    {
        if (_anchor.IsEmpty || _anchor.Width <= 0) return;
        var work = ScreenMetrics.WorkAreaDip();
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : 120;
        double catCenter = _anchor.Left + _anchor.Width / 2;
        double headTop = _anchor.Top + _anchor.Height * 3 / 32.0;   // ear tips are on sprite row 3

        double left = Math.Clamp(catCenter - w * 0.5, work.Left + 4, Math.Max(work.Left + 4, work.Right - w - 4));
        double above = headTop - h + 2, below = _anchor.Bottom + 2;
        bool fitsAbove = above >= work.Top + 2, fitsBelow = below + h <= work.Bottom - 2;
        // hysteresis: only switch sides when the current one no longer fits (no flicker mid-jump)
        if (_below ? !fitsBelow && fitsAbove : !fitsAbove && fitsBelow) _below = !_below;
        Left = left;
        Top = Math.Clamp(_below ? below : above, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - h - 2));
        Frame.TailOnTop = _below;
        Frame.TailX = catCenter - left;
    }

    // ------------------------------------------------------------------ conversation

    public void BeginExchange(string userText)
    {
        ReplyText.Inlines.Clear();
        ReplyText.Inlines.Add(new Run("> " + userText) { Foreground = Palette.Muted });
        ReplyText.Inlines.Add(new LineBreak());
        _replyRun = null;
        ReplyScroll.Visibility = Visibility.Visible;
        UpdateReplyAutomation();
    }

    public void AppendToken(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_replyRun == null)
        {
            _replyRun = new Run();
            ReplyText.Inlines.Add(_replyRun);
            text = text.TrimStart('\r', '\n');
        }
        _replyRun.Text += text;
        ReplyScroll.Visibility = Visibility.Visible;
        ReplyScroll.ScrollToEnd();
        UpdateReplyAutomation();
    }

    /// <summary>Empties the message log.</summary>
    public void ClearReply()
    {
        ReplyText.Inlines.Clear();
        _replyRun = null;
        UpdateReplyAutomation();
    }

    /// <summary>A side note from Miri herself (stop notices, errors).</summary>
    public void AppendNote(string text)
    {
        if (ReplyText.Inlines.Count > 0) ReplyText.Inlines.Add(new LineBreak());
        ReplyText.Inlines.Add(new Run(text) { FontStyle = FontStyles.Italic, Foreground = Palette.Muted });
        _replyRun = null;
        ReplyScroll.Visibility = Visibility.Visible;
        ReplyScroll.ScrollToEnd();
        UpdateReplyAutomation();
    }

    public void SetStatus(string text) => StatusText.Text = text ?? "";

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;
        var text = Input.Text.Trim();
        if (text.Length == 0) return;
        Input.Clear();
        Submitted?.Invoke(text);
    }

    // ------------------------------------------------------------------ approval card

    public Task<ApprovalDecision> AskApproval(ApprovalRequest request, CancellationToken ct)
    {
        Answer(ApprovalDecision.No);   // never leave an older card dangling

        bool danger = request.Risk.Level == RiskLevel.Dangerous;
        ApprovalTitle.Text = string.IsNullOrWhiteSpace(request.Description) ? Describe(request.Action) : request.Description;
        ApprovalReason.Text = danger
            ? "Careful: " + (string.IsNullOrWhiteSpace(request.Risk.Reason) ? "this can't be undone." : request.Risk.Reason)
            : request.Risk.Reason ?? "";
        ApprovalReason.Visibility = string.IsNullOrWhiteSpace(ApprovalReason.Text) ? Visibility.Collapsed : Visibility.Visible;
        ApprovalReason.Foreground = danger ? Palette.Danger : Palette.Muted;
        YesAllButton.Visibility = danger ? Visibility.Collapsed : Visibility.Visible;
        YesButton.Background = danger ? Palette.DangerShade : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE3, 0xE6, 0xB8));
        YesButton.BorderBrush = danger ? Palette.Danger : Palette.Outline;
        ApprovalCard.Outline = danger ? Palette.Danger : Palette.Outline;
        ApprovalCard.Shade = danger ? Palette.DangerShade : Palette.Shade;
        Frame.Outline = danger ? Palette.Danger : Palette.Outline;
        ApprovalCard.Visibility = Visibility.Visible;

        var tcs = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approval = tcs;
        _approvalReg = ct.Register(() => Dispatcher.BeginInvoke(() =>
        {
            if (_approval == tcs) CloseCard();
            tcs.TrySetCanceled(ct);
        }));
        NoButton.Focus();   // a stray Enter means "no"
        return tcs.Task;
    }

    private void Answer(ApprovalDecision decision)
    {
        var tcs = _approval;
        if (tcs == null) return;
        CloseCard();
        tcs.TrySetResult(decision);
    }

    private void CloseCard()
    {
        _approval = null;
        _approvalReg.Dispose();
        ApprovalCard.Visibility = Visibility.Collapsed;
        Frame.Outline = Palette.Outline;
        if (IsVisible) Input.Focus();
    }

    private static string Describe(UiAction a) => a.Kind switch
    {
        ActionKind.Click => $"Click element #{a.ElementId}?",
        ActionKind.TypeText => $"Type \"{a.Text}\" into #{a.ElementId}?",
        ActionKind.PressKeys => $"Press {a.Keys}?",
        ActionKind.Scroll => $"Scroll #{a.ElementId} by {a.ScrollAmount}?",
        ActionKind.FocusWindow => $"Switch to \"{a.Target}\"?",
        ActionKind.OpenApp => $"Open {a.Target}?",
        _ => a.Kind + "?",
    };
}
