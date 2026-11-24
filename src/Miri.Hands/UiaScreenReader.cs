using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Miri.Core;

namespace Miri.Hands;

/// <summary>
/// Reads the foreground window through UI Automation (FlaUI UIA3).
/// All UIA work runs on thread-pool (MTA) threads, so it is safe to call from any thread,
/// including the WPF UI thread. Snapshots are bounded (400 nodes / 1.5 s) and never hang the caller.
/// </summary>
public sealed class UiaScreenReader : IScreenReader, IDisposable
{
    private const int MaxNodes = 400;
    private const int MaxElements = 60;
    private const int MaxElementsAll = 300;
    private static readonly TimeSpan WalkBudget = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CallerTimeout = TimeSpan.FromMilliseconds(2500);

    private static readonly HashSet<ControlType> KeptTypes = new()
    {
        ControlType.Button, ControlType.Edit, ControlType.Hyperlink, ControlType.ListItem,
        ControlType.MenuItem, ControlType.TabItem, ControlType.CheckBox, ControlType.RadioButton,
        ControlType.ComboBox, ControlType.TreeItem, ControlType.Text, ControlType.Document,
        ControlType.SplitButton,
    };

    private readonly object _automationLock = new();
    private readonly object _mapLock = new();
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private UIA3Automation? _automation;
    private Dictionary<int, AutomationElement> _map = new();
    private UiSnapshot? _latest;
    private bool _disposed;

    public UiaScreenReader() { }

    /// <summary>The shared automation object (created lazily on an MTA thread).</summary>
    internal UIA3Automation Automation
    {
        get
        {
            lock (_automationLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _automation ??= new UIA3Automation();
            }
        }
    }

    /// <summary>The most recent snapshot produced by this reader (its Ids are resolvable).</summary>
    public UiSnapshot? Latest
    {
        get { lock (_mapLock) return _latest; }
    }

    /// <summary>Resolves an element Id from the LATEST snapshot. Use the element only from MTA/background threads.</summary>
    public AutomationElement? Resolve(int id)
    {
        lock (_mapLock)
            return _map.TryGetValue(id, out var el) ? el : null;
    }

    internal bool IsLatest(UiSnapshot snapshot)
    {
        lock (_mapLock) return ReferenceEquals(_latest, snapshot);
    }

    public UiSnapshot? SnapshotForeground(IntPtr excludeHwnd)
    {
        var hwnd = PickTargetWindow(excludeHwnd);
        return hwnd == IntPtr.Zero ? null : SnapshotWindow(hwnd);
    }

    public UiSnapshot? SnapshotForegroundAll(IntPtr excludeHwnd)
    {
        var hwnd = PickTargetWindow(excludeHwnd);
        return hwnd == IntPtr.Zero ? null : SnapshotWindow(hwnd, MaxElementsAll);
    }

    /// <summary>Snapshots a specific top-level window. Returns null on failure or timeout.</summary>
    public UiSnapshot? SnapshotWindow(IntPtr hwnd) => SnapshotWindow(hwnd, MaxElements);

    private UiSnapshot? SnapshotWindow(IntPtr hwnd, int maxElements)
    {
        if (_disposed || !NativeMethods.IsAlive(hwnd)) return null;
        if (!_snapshotGate.Wait(CallerTimeout)) return null;   // a previous snapshot is stuck
        var released = false;
        try
        {
            var task = Task.Run(() =>
            {
                try { return Capture(hwnd, maxElements); }
                finally { _snapshotGate.Release(); }
            });
            released = true;   // the task owns the gate now
            return task.Wait(CallerTimeout) ? task.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (!released) _snapshotGate.Release();
        }
    }

    public IReadOnlyList<(IntPtr Hwnd, string Title, string Process)> ListWindows()
    {
        var result = new List<(IntPtr, string, string)>();
        var names = new Dictionary<uint, string>();
        var self = (uint)Environment.ProcessId;
        try
        {
            foreach (var h in NativeMethods.TopLevelWindows())
            {
                if (!NativeMethods.IsAppWindow(h, requireTitle: true)) continue;
                var pid = NativeMethods.ProcessIdOf(h);
                if (pid == self) continue;
                if (!names.TryGetValue(pid, out var name))
                    names[pid] = name = NativeMethods.ProcessNameOf(pid);
                result.Add((h, NativeMethods.TitleOf(h), name));
            }
        }
        catch (Exception)
        {
            // best effort
        }
        return result;
    }

    private static IntPtr PickTargetWindow(IntPtr excludeHwnd)
    {
        var self = (uint)Environment.ProcessId;
        var excludedPid = NativeMethods.ProcessIdOf(excludeHwnd);

        bool Excluded(IntPtr h)
        {
            if (h == excludeHwnd) return true;
            var pid = NativeMethods.ProcessIdOf(h);
            return pid == self || (excludedPid != 0 && pid == excludedPid);
        }

        var fg = NativeMethods.Foreground();
        if (fg != IntPtr.Zero && !Excluded(fg) && NativeMethods.IsAppWindow(fg, requireTitle: false))
            return fg;

        foreach (var h in NativeMethods.ZOrderFrom(fg))
        {
            if (!Excluded(h) && NativeMethods.IsAppWindow(h, requireTitle: true)) return h;
        }
        return IntPtr.Zero;
    }

    private UiSnapshot? Capture(IntPtr hwnd, int maxElements)
    {
        var automation = Automation;
        var pid = NativeMethods.ProcessIdOf(hwnd);
        var processName = NativeMethods.ProcessNameOf(pid);
        var title = NativeMethods.TitleOf(hwnd);
        var windowBounds = NativeMethods.WindowRect(hwnd);

        var found = new List<(UiElementInfo Info, AutomationElement Element)>();
        try
        {
            var root = automation.FromHandle(hwnd);
            try
            {
                var rb = root.BoundingRectangle;
                if (!rb.IsEmpty) windowBounds = rb;   // UIA reports physical pixels
                if (string.IsNullOrEmpty(title)) title = root.Properties.Name.ValueOrDefault ?? "";
            }
            catch (Exception) { /* keep Win32 values */ }

            Walk(automation, root, windowBounds, found);
        }
        catch (Exception)
        {
            // Elevated or dying windows: return what we have (possibly an empty snapshot).
        }

        var ordered = found
            .GroupBy(f => (f.Info.ControlType, f.Info.Name, f.Info.Bounds))
            .Select(g => g.First())
            .OrderBy(f => f.Info.Bounds.Top / 8)      // rows (small vertical jitter tolerated)
            .ThenBy(f => f.Info.Bounds.Left)
            .Take(maxElements)
            .ToList();

        var elements = new List<UiElementInfo>(ordered.Count);
        var map = new Dictionary<int, AutomationElement>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var id = i + 1;
            elements.Add(ordered[i].Info with { Id = id });
            map[id] = ordered[i].Element;
        }

        var snapshot = new UiSnapshot(title, processName, hwnd, windowBounds, elements);
        lock (_mapLock)
        {
            _map = map;
            _latest = snapshot;
        }
        return snapshot;
    }

    private static void Walk(UIA3Automation automation, AutomationElement root, Rectangle windowBounds,
        List<(UiElementInfo, AutomationElement)> found)
    {
        var walker = automation.TreeWalkerFactory.GetControlViewWalker();
        var sw = Stopwatch.StartNew();
        var visited = 0;
        var stack = new Stack<AutomationElement>();
        PushChildrenReversed(walker, root, stack);

        while (stack.Count > 0 && visited < MaxNodes && sw.Elapsed < WalkBudget)
        {
            var el = stack.Pop();
            visited++;
            bool descend = true;
            try
            {
                var info = TryDescribe(el, windowBounds, out descend);
                if (info != null) found.Add((info, el));
            }
            catch (Exception)
            {
                continue;   // element vanished or access denied
            }
            if (descend && sw.Elapsed < WalkBudget)
                PushChildrenReversed(walker, el, stack);
        }
    }

    private static void PushChildrenReversed(FlaUI.Core.ITreeWalker walker, AutomationElement parent,
        Stack<AutomationElement> stack)
    {
        var children = new List<AutomationElement>();
        try
        {
            var c = walker.GetFirstChild(parent);
            while (c != null && children.Count < 200)
            {
                children.Add(c);
                c = walker.GetNextSibling(c);
            }
        }
        catch (Exception) { /* ignore */ }
        for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
    }

    private static UiElementInfo? TryDescribe(AutomationElement el, Rectangle windowBounds, out bool descend)
    {
        descend = true;
        var props = el.Properties;
        var type = props.ControlType.ValueOrDefault;

        // Text-only leaves and combo boxes: their children are rarely useful and cost nodes.
        if (type is ControlType.Text or ControlType.Hyperlink or ControlType.Button) descend = false;

        if (!KeptTypes.Contains(type)) return null;

        var bounds = props.BoundingRectangle.ValueOrDefault;
        if (bounds.IsEmpty || bounds.Width <= 1 || bounds.Height <= 1) return null;
        if (props.IsOffscreen.ValueOrDefault) return null;
        if (!windowBounds.IsEmpty && !windowBounds.IntersectsWith(bounds)) return null;

        var name = (props.Name.ValueOrDefault ?? "").Trim();
        var enabled = props.IsEnabled.ValueOrDefault;
        if (type == ControlType.Text)
        {
            if (name.Length == 0) return null;
        }
        else if (!enabled)
        {
            return null;
        }

        var isPassword = props.IsPassword.ValueOrDefault;
        string? value = null;
        if (!isPassword)
        {
            if (type is ControlType.Edit or ControlType.ComboBox or ControlType.Document)
            {
                var vp = el.Patterns.Value.PatternOrDefault;
                if (vp != null) value = Clip(vp.Value.ValueOrDefault);
            }
            else if (type is ControlType.CheckBox or ControlType.RadioButton)
            {
                var tp = el.Patterns.Toggle.PatternOrDefault;
                if (tp != null) value = tp.ToggleState.ValueOrDefault == ToggleState.On ? "checked" : "unchecked";
                else
                {
                    var sp = el.Patterns.SelectionItem.PatternOrDefault;
                    if (sp != null) value = sp.IsSelected.ValueOrDefault ? "checked" : "unchecked";
                }
            }
        }

        if (name.Length > 120) name = name[..120];
        return new UiElementInfo(0, type.ToString(), name, value, bounds, isPassword, enabled);
    }

    private static string? Clip(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Length > 200 ? s[..200] : s;
    }

    public void Dispose()
    {
        lock (_automationLock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _automation?.Dispose(); } catch (Exception) { }
            _automation = null;
        }
        lock (_mapLock)
        {
            _map = new();
            _latest = null;
        }
    }
}
