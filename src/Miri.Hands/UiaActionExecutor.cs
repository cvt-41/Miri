using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Exceptions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Miri.Core;

namespace Miri.Hands;

/// <summary>
/// Executes approved UI actions through UI Automation, with mouse/keyboard fallbacks.
/// Never throws except <see cref="OperationCanceledException"/>; failures come back as ActionResult(false, ...).
/// </summary>
public sealed class UiaActionExecutor : IActionExecutor
{
    private const string AdminMessage = "That window runs as admin, I can't touch it";
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(3);

    private static readonly Dictionary<string, string> AppAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["notepad"] = "notepad", ["note pad"] = "notepad", ["sổ ghi chép"] = "notepad",
        ["calculator"] = "calc", ["calc"] = "calc", ["máy tính"] = "calc", ["may tinh"] = "calc",
        ["paint"] = "mspaint", ["mspaint"] = "mspaint",
        ["explorer"] = "explorer", ["file explorer"] = "explorer", ["files"] = "explorer",
        ["cmd"] = "cmd", ["command prompt"] = "cmd",
        ["task manager"] = "taskmgr", ["trình quản lý tác vụ"] = "taskmgr",
        ["thư mục"] = "explorer",
        ["cài đặt"] = "ms-settings:", ["cai dat"] = "ms-settings:",
        ["settings"] = "ms-settings:",   // shell protocol, built-in only (model input can't contain ':')
    };

    private readonly MiriSettings _settings;
    private readonly UiaScreenReader _reader;

    /// <summary>Called with an element's physical bounds right before Miri clicks or types on it; completes when the kitten is there.</summary>
    public Func<Rectangle, Task>? BeforeTouch { get; set; }

    private void Touch(AutomationElement? el, CancellationToken ct)
    {
        if (BeforeTouch == null || el == null) return;
        var b = SafeBounds(el);
        if (b.IsEmpty) return;
        try { BeforeTouch(b).Wait(6500, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the kitten is decoration: never fail the action over her */ }
        ct.WaitHandle.WaitOne(150);   // a beat so her tap is visible before the click
        ct.ThrowIfCancellationRequested();
    }

    public UiaActionExecutor(MiriSettings settings, UiaScreenReader reader)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        AppLocator.Warm();   // load the Start-menu app list in the background
    }

    public ActionRisk Assess(UiAction action, UiSnapshot snapshot) => SafetyPolicy.Assess(action, snapshot, _settings);

    public async Task<ActionResult> ExecuteAsync(UiAction action, UiSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ActionResult result;
        try
        {
            if (action == null || snapshot == null)
            {
                result = new ActionResult(false, "I don't have an action or a screen snapshot to work with.");
            }
            else
            {
                var risk = Assess(action, snapshot);
                result = risk.Level == RiskLevel.Blocked
                    ? new ActionResult(false, "I won't do that: " + risk.Reason)
                    : await Task.Run(() => ExecuteCore(action, snapshot, ct), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            if (action != null) ActionLog.Append(snapshot, action, new ActionResult(false, "Cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            result = Friendly(ex);
        }

        if (action != null) ActionLog.Append(snapshot, action, result);
        return result;
    }

    private static ActionResult Friendly(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException != null) ex = agg.InnerException;
        return ex switch
        {
            HandsException h => new ActionResult(false, h.Message),
            UnauthorizedAccessException => new ActionResult(false, AdminMessage),
            COMException c when (uint)c.HResult == 0x80070005 => new ActionResult(false, AdminMessage),
            ElementNotAvailableException => new ActionResult(false, "That element is gone. The screen probably changed, let me look again."),
            COMException c when (uint)c.HResult == 0x80040201 => new ActionResult(false, "That element is gone. The screen probably changed, let me look again."),
            PatternNotSupportedException => new ActionResult(false, "That control doesn't support this action."),
            TimeoutException => new ActionResult(false, "The app didn't respond in time."),
            _ => new ActionResult(false, "Something went wrong while doing that: " + ex.Message),
        };
    }

    // ------------------------------------------------------------------

    private ActionResult ExecuteCore(UiAction action, UiSnapshot snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (action.Kind)
        {
            case ActionKind.OpenApp:
                return OpenApp(action.Target, ct);
            case ActionKind.FocusWindow:
                return FocusWindow(action.Target, snapshot);
        }

        if (action.Kind == ActionKind.PressKeys && action.ElementId == null && KeyParser.IsGlobalMediaOnly(action.Keys))
        {
            KeyParser.TryParse(action.Keys, out var media, out _);
            foreach (var chord in media) { Keyboard.TypeSimultaneously(chord.Keys.ToArray()); Thread.Sleep(30); }
            return new ActionResult(true, $"Pressed {string.Join(" ", media.Select(c => c.Canonical))}.");
        }

        if (!NativeMethods.IsAlive(snapshot.Hwnd))
            return new ActionResult(false, "That window is closed now.");
        if (NativeMethods.IsBlockedByElevation(snapshot.Hwnd))
            return new ActionResult(false, AdminMessage);

        var element = ResolveElement(action, snapshot);
        ct.ThrowIfCancellationRequested();
        if (action.Kind is ActionKind.Click or ActionKind.TypeText) Touch(element, ct);

        return action.Kind switch
        {
            ActionKind.Click => Click(element, snapshot, ct),
            ActionKind.TypeText => TypeText(element, action.Text, snapshot, ct),
            ActionKind.PressKeys => PressKeys(element, action.Keys, snapshot, ct),
            ActionKind.Scroll => Scroll(element, action.ScrollAmount, snapshot),
            _ => new ActionResult(false, $"I don't know how to do {action.Kind}."),
        };
    }

    private AutomationElement? ResolveElement(UiAction action, UiSnapshot snapshot)
    {
        if (action.ElementId is not int id) return null;
        var info = snapshot.Elements.FirstOrDefault(e => e.Id == id)
                   ?? throw new HandsException($"There's no element [{id}] on the screen I looked at.");

        if (_reader.IsLatest(snapshot))
        {
            var el = _reader.Resolve(id);
            if (el != null) return el;
        }

        // The snapshot is older than the reader's latest one: find the element again by name/type/position.
        var root = _reader.Automation.FromHandle(snapshot.Hwnd);
        var cf = _reader.Automation.ConditionFactory;
        FlaUI.Core.Conditions.ConditionBase condition = Enum.TryParse<ControlType>(info.ControlType, out var ctype)
            ? cf.ByControlType(ctype).And(cf.ByName(info.Name))
            : cf.ByName(info.Name);
        var candidates = root.FindAllDescendants(condition);
        var center = Center(info.Bounds);
        var best = candidates
            .Select(c => (c, d: Distance(Center(SafeBounds(c)), center)))
            .OrderBy(t => t.d)
            .FirstOrDefault();
        if (best.c == null || best.d > 200)
            throw new HandsException($"I can't find \"{info.Name}\" anymore. The screen probably changed.");
        return best.c;
    }

    // ---------------- Click ----------------

    private ActionResult Click(AutomationElement? el, UiSnapshot snapshot, CancellationToken ct)
    {
        if (el == null) return new ActionResult(false, "Click needs an element id.");
        var name = SafeName(el);

        var invoke = el.Patterns.Invoke.PatternOrDefault;
        if (invoke != null)
        {
            // Invoke can block while a modal dialog it opened is shown; don't wait forever.
            var task = Task.Run(() => invoke.Invoke(), ct);
            try
            {
                if (task.Wait(InvokeTimeout, ct))
                    return new ActionResult(true, $"Clicked \"{name}\".");
                return new ActionResult(true, $"Clicked \"{name}\" (a dialog may have opened).");
            }
            catch (AggregateException ex) when (ex.InnerException is not OperationCanceledException)
            {
                // fall back to a real mouse click below
                if (IsAccessDenied(ex.InnerException)) return new ActionResult(false, AdminMessage);
            }
        }

        var bounds = SafeBounds(el);
        if (bounds.IsEmpty) return new ActionResult(false, $"\"{name}\" isn't visible, so I can't click it.");
        var point = Center(bounds);

        if (!NativeMethods.ForceForeground(snapshot.Hwnd))
            return new ActionResult(false, "I couldn't bring that window to the front.");
        ct.ThrowIfCancellationRequested();

        if (!PointBelongsTo(point, snapshot.Hwnd))
            return new ActionResult(false, "Something else is covering that spot, so I didn't click.");

        Mouse.Click(point, MouseButton.Left);
        return new ActionResult(true, $"Clicked \"{name}\".");
    }

    // ---------------- TypeText ----------------

    private ActionResult TypeText(AutomationElement? el, string? text, UiSnapshot snapshot, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text)) return new ActionResult(false, "There's no text to type.");
        var hasNewline = text.Contains('\n') || text.Contains('\r');

        if (el != null && !hasNewline)
        {
            var vp = el.Patterns.Value.PatternOrDefault;
            if (vp != null && !vp.IsReadOnly.ValueOrDefault)
            {
                try
                {
                    vp.SetValue(text);
                    return new ActionResult(true, $"Typed into \"{SafeName(el)}\".");
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !IsAccessDenied(ex))
                {
                    // fall back to the keyboard
                }
            }
        }

        if (!NativeMethods.ForceForeground(snapshot.Hwnd))
            return new ActionResult(false, "I couldn't bring that window to the front, so I didn't type.");
        if (el != null)
        {
            try { el.Focus(); } catch (Exception) { /* keyboard focus best effort */ }
        }
        ct.ThrowIfCancellationRequested();

        // Make sure we are still typing into the right app.
        if (NativeMethods.ProcessIdOf(NativeMethods.Foreground()) != NativeMethods.ProcessIdOf(snapshot.Hwnd))
            return new ActionResult(false, "The window lost focus, so I stopped typing.");

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (lines[i].Length > 0) Keyboard.Type(lines[i]);
            if (i < lines.Length - 1) Keyboard.Type(VirtualKeyShort.RETURN);
        }
        return new ActionResult(true, el != null ? $"Typed into \"{SafeName(el)}\"." : "Typed the text.");
    }

    // ---------------- PressKeys ----------------

    private static ActionResult PressKeys(AutomationElement? el, string? keys, UiSnapshot snapshot, CancellationToken ct)
    {
        if (!KeyParser.TryParse(keys, out var chords, out var error)) return new ActionResult(false, error);

        if (!NativeMethods.ForceForeground(snapshot.Hwnd))
            return new ActionResult(false, "I couldn't bring that window to the front, so I didn't press anything.");
        if (el != null)
        {
            try { el.Focus(); } catch (Exception) { /* best effort */ }
        }

        foreach (var chord in chords)
        {
            ct.ThrowIfCancellationRequested();
            if (NativeMethods.ProcessIdOf(NativeMethods.Foreground()) != NativeMethods.ProcessIdOf(snapshot.Hwnd))
                return new ActionResult(false, "The window lost focus, so I stopped pressing keys.");
            Keyboard.TypeSimultaneously(chord.Keys.ToArray());
            Thread.Sleep(30);
        }
        return new ActionResult(true, $"Pressed {string.Join(" ", chords.Select(c => c.Canonical))}.");
    }

    // ---------------- Scroll ----------------

    /// <summary>Positive amount scrolls down, negative scrolls up (in wheel notches / small steps).</summary>
    private ActionResult Scroll(AutomationElement? el, int amount, UiSnapshot snapshot)
    {
        if (amount == 0) return new ActionResult(false, "Scroll amount is zero.");
        var steps = Math.Min(Math.Abs(amount), 50);
        var direction = amount > 0 ? "down" : "up";

        var target = el ?? _reader.Automation.FromHandle(snapshot.Hwnd);
        var sp = target.Patterns.Scroll.PatternOrDefault;
        if (sp != null && sp.VerticallyScrollable.ValueOrDefault)
        {
            var step = amount > 0 ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
            for (var i = 0; i < steps; i++) sp.Scroll(ScrollAmount.NoAmount, step);
            return new ActionResult(true, $"Scrolled {direction}.");
        }

        var bounds = el != null ? SafeBounds(el) : snapshot.WindowBounds;
        if (bounds.IsEmpty) return new ActionResult(false, "I can't see where to scroll.");
        if (!NativeMethods.ForceForeground(snapshot.Hwnd))
            return new ActionResult(false, "I couldn't bring that window to the front.");
        var point = Center(bounds);
        if (!PointBelongsTo(point, snapshot.Hwnd))
            return new ActionResult(false, "Something else is covering that spot, so I didn't scroll.");
        Mouse.MoveTo(point);
        Mouse.Scroll(-Math.Sign(amount) * steps);   // FlaUI: positive = wheel up
        return new ActionResult(true, $"Scrolled {direction}.");
    }

    // ---------------- FocusWindow ----------------

    private ActionResult FocusWindow(string? target, UiSnapshot snapshot)
    {
        IntPtr hwnd;
        string title;
        if (string.IsNullOrWhiteSpace(target))
        {
            hwnd = snapshot.Hwnd;
            title = snapshot.WindowTitle;
        }
        else
        {
            var t = target.Trim();
            var windows = _reader.ListWindows();
            var match = windows.FirstOrDefault(w => string.Equals(w.Title, t, StringComparison.OrdinalIgnoreCase));
            if (match.Hwnd == IntPtr.Zero)
                match = windows.FirstOrDefault(w => w.Title.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (match.Hwnd == IntPtr.Zero)
                match = windows.FirstOrDefault(w => string.Equals(w.Process, t, StringComparison.OrdinalIgnoreCase));
            if (match.Hwnd == IntPtr.Zero)
            {
                var folded = SafetyPolicy.StripDiacritics(t);
                match = windows.FirstOrDefault(w =>
                    SafetyPolicy.StripDiacritics(w.Title).Contains(folded, StringComparison.OrdinalIgnoreCase));
            }
            if (match.Hwnd == IntPtr.Zero) return new ActionResult(false, $"I can't find a window called \"{t}\".");

            // the policy checked the target text; check the real window too
            var risk = SafetyPolicy.Assess(new UiAction(ActionKind.FocusWindow, Target: match.Title + " " + match.Process),
                snapshot, _settings);
            if (risk.Level == RiskLevel.Blocked) return new ActionResult(false, "I won't do that: " + risk.Reason);
            hwnd = match.Hwnd;
            title = match.Title;
        }

        if (!NativeMethods.IsAlive(hwnd)) return new ActionResult(false, "That window is closed now.");
        return NativeMethods.ForceForeground(hwnd)
            ? new ActionResult(true, $"Switched to \"{title}\".")
            : new ActionResult(false, $"Windows didn't let me bring \"{title}\" to the front.");
    }

    // ---------------- OpenApp ----------------

    private ActionResult OpenApp(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return new ActionResult(false, "Which app should I open?");
        var n = name.Trim().Trim('"', '\'');
        if (n.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || n.Contains("http", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("..") || n.Length > 60 || n.Any(char.IsControl))
            return new ActionResult(false, "I only open apps by plain name, not paths or links.");

        string opened = n;
        if (AppAliases.TryGetValue(n, out var builtin))
        {
            if (!TryShellStart(builtin)) return new ActionResult(false, $"I couldn't open \"{n}\".");
        }
        else
        {
            AppLocator.AppEntry? app;
            IReadOnlyList<string> suggestions;
            try { app = AppLocator.Find(n, out suggestions); }
            catch (Exception) { app = null; suggestions = Array.Empty<string>(); }

            if (app != null)
            {
                // like a person: its taskbar icon, else Start search; direct launch only if both fail
                try { if (!OpenFromTaskbar(app.Name, ct) && !OpenFromStart(app.Name, ct)) AppLocator.Launch(app); opened = app.Name; }
                catch (Exception) { return new ActionResult(false, $"I found \"{app.Name}\" but couldn't start it."); }
            }
            else if (!TryShellStart(n))   // last try: exe names registered in App Paths / PATH (e.g. "code", "chrome")
            {
                var hint = suggestions.Count > 0 ? " Did you mean " + string.Join(", ", suggestions.Select(x => $"\"{x}\"")) + "?" : "";
                return new ActionResult(false, $"I couldn't find an app called \"{n}\" on this PC.{hint}");
            }
        }

        // give the new window a moment to show up before the next snapshot
        for (var i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(100);
        }
        return new ActionResult(true, $"Opened {opened}.");
    }

    private AutomationElement? TaskbarButton(Func<string, bool> match)
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;
        var cf = _reader.Automation.ConditionFactory;
        return _reader.Automation.FromHandle(tray).FindAllDescendants(cf.ByControlType(ControlType.Button))
            .FirstOrDefault(b => match(SafeName(b)));
    }

    /// <summary>Clicks the app's pinned taskbar icon. Win10 names them "Sonora" or "Sonora - 1 running window".</summary>
    private bool OpenFromTaskbar(string appName, CancellationToken ct)
    {
        AutomationElement? btn;
        try { btn = TaskbarButton(n => n.Equals(appName, StringComparison.OrdinalIgnoreCase) || n.StartsWith(appName + " - ", StringComparison.OrdinalIgnoreCase)); }
        catch (Exception) { return false; }
        if (btn == null) return false;
        Touch(btn, ct);
        try { btn.Patterns.Invoke.Pattern.Invoke(); }
        catch (Exception) { var b = SafeBounds(btn); if (b.IsEmpty) return false; Mouse.Click(Center(b), MouseButton.Left); }
        return true;
    }

    private static readonly string[] StartProcesses = { "SearchApp", "SearchUI", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost" };

    /// <summary>Win key, type the app's name, Enter. Types only once the Start/search UI is really in front.</summary>
    private bool OpenFromStart(string appName, CancellationToken ct)
    {
        try { Touch(TaskbarButton(n => n == "Start"), ct); } catch (Exception) { }
        Keyboard.Type(VirtualKeyShort.LWIN);
        bool StartInFront()
        {
            try { return StartProcesses.Contains(Process.GetProcessById((int)NativeMethods.ProcessIdOf(NativeMethods.Foreground())).ProcessName, StringComparer.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
        for (int i = 0; i < 15 && !StartInFront(); i++) { ct.ThrowIfCancellationRequested(); Thread.Sleep(100); }
        if (!StartInFront()) return false;
        Keyboard.Type(appName);
        ct.WaitHandle.WaitOne(1200);   // let search find it
        ct.ThrowIfCancellationRequested();
        if (!StartInFront()) return false;
        Keyboard.Type(VirtualKeyShort.RETURN);
        return true;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);

    private static bool TryShellStart(string target)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ---------------- helpers ----------------

    private bool PointBelongsTo(Point point, IntPtr hwnd)
    {
        try
        {
            var hit = _reader.Automation.FromPoint(point);
            var hitPid = (uint)hit.Properties.ProcessId.ValueOrDefault;
            return hitPid == NativeMethods.ProcessIdOf(hwnd) || hitPid == (uint)Environment.ProcessId || hitPid == 0;
        }
        catch (Exception)
        {
            return true;   // can't tell: trust the snapshot
        }
    }

    private static bool IsAccessDenied(Exception? ex) =>
        ex is UnauthorizedAccessException || (ex is COMException c && (uint)c.HResult == 0x80070005);

    private static string SafeName(AutomationElement el)
    {
        try { return el.Properties.Name.ValueOrDefault ?? ""; }
        catch (Exception) { return ""; }
    }

    private static Rectangle SafeBounds(AutomationElement el)
    {
        try { return el.Properties.BoundingRectangle.ValueOrDefault; }
        catch (Exception) { return Rectangle.Empty; }
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

/// <summary>An expected failure whose message is safe to show the user.</summary>
internal sealed class HandsException(string message) : Exception(message);
