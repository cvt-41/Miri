using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Miri.Hands;

/// <summary>
/// The few desktop moves Miri's mischief may make: look at the window in front, Ctrl+Tab a browser,
/// bring another app forward. Never types text, clicks content, closes or minimizes anything.
/// All rects are physical pixels.
/// </summary>
public static class Tease
{
    public sealed record Window(IntPtr Hwnd, string Process, string Title, Rectangle Bounds);

    private static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "brave", "helium", "vivaldi", "opera", "chromium", "arc", "thorium", "yandex", "browser",
    };

    public static bool IsChromium(string process) => Chromium.Contains(process);

    /// <summary>The foreground window if it's a normal, visible, titled app window that isn't ours, fullscreen or elevated.</summary>
    public static Window? Foreground(uint ownPid)
    {
        var w = Describe(NativeMethods.Foreground(), ownPid);
        return w == null || IsFullscreen(w.Hwnd, w.Bounds) ? null : w;
    }

    /// <summary>The next suitable app window below <paramref name="hwnd"/> in Z-order (not minimized, not ours, not elevated).</summary>
    public static Window? NextBelow(IntPtr hwnd, uint ownPid)
    {
        foreach (var h in NativeMethods.ZOrderFrom(hwnd))
            if (Describe(h, ownPid) is { } w && NativeMethods.ProcessIdOf(h) != NativeMethods.ProcessIdOf(hwnd)) return w;
        return null;
    }

    /// <summary>Bounds of <paramref name="w"/>'s taskbar button (Win10 names: "App", "App - 2 running windows", or the title); empty if not found.</summary>
    public static Rectangle TaskbarButton(Window w)
    {
        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return Rectangle.Empty;
            string friendly = FriendlyName(w);
            bool Match(string n, string want) => want.Length > 0 &&
                (n.Equals(want, StringComparison.OrdinalIgnoreCase) || n.StartsWith(want + " - ", StringComparison.OrdinalIgnoreCase));
            using var automation = new UIA3Automation();
            var btn = automation.FromHandle(tray).FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Button))
                .FirstOrDefault(b =>
                {
                    string n;
                    try { n = b.Properties.Name.ValueOrDefault ?? ""; } catch { n = ""; }
                    return Match(n, friendly) || Match(n, w.Title);
                });
            return btn == null ? Rectangle.Empty : btn.BoundingRectangle;
        }
        catch (Exception)
        {
            return Rectangle.Empty;
        }
    }

    /// <summary>Ctrl+Tab, only if <paramref name="hwnd"/> is still the foreground window.</summary>
    public static bool NextTab(IntPtr hwnd)
    {
        if (NativeMethods.Foreground() != hwnd) return false;
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.TAB);
        return true;
    }

    public static bool SwitchTo(IntPtr hwnd) => NativeMethods.ForceForeground(hwnd);

    public static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    private static readonly HashSet<string> MusicApps = new(StringComparer.OrdinalIgnoreCase) { "spotify", "sonora", "music", "musicbee", "aimp", "foobar2000", "vlc", "wmplayer", "groove" };

    /// <summary>Is <paramref name="w"/> something playing media: a music app, or YouTube in a browser?</summary>
    public static bool IsMedia(Window w) =>
        (MusicApps.Contains(w.Process) || (IsChromium(w.Process) && w.Title.Contains("YouTube", StringComparison.OrdinalIgnoreCase))) &&
        !w.Title.Contains("🔴");   // red circle = live stream: never seek those

    /// <summary>A visible YouTube window or music player behind <paramref name="hwnd"/> (top-most first); null if none.</summary>
    public static Window? FindMediaWindow(IntPtr hwnd, uint ownPid)
    {
        foreach (var h in NativeMethods.ZOrderFrom(hwnd))
            if (Describe(h, ownPid) is { } w && IsMedia(w)) return w;
        return null;
    }

    /// <summary>The player's progress bar (a UIA slider such as Sonora's "Position" or YouTube's "Seek slider"); null if none.</summary>
    public static SeekBar? FindSeekBar(Window w)
    {
        var automation = new UIA3Automation();
        try
        {
            var bar = automation.FromHandle(w.Hwnd).FindAllDescendants(automation.ConditionFactory.ByControlType(ControlType.Slider))
                .FirstOrDefault(sl =>
                {
                    try
                    {
                        var n = (sl.Properties.Name.ValueOrDefault ?? "").ToLowerInvariant();
                        var rv = sl.Patterns.RangeValue.PatternOrDefault;
                        return SeekBar.LooksLikeProgress(n) && rv != null && !rv.IsReadOnly.Value && rv.Maximum.Value > rv.Minimum.Value &&
                               !sl.Properties.IsOffscreen.ValueOrDefault && sl.BoundingRectangle.Width >= 100;
                    }
                    catch (Exception) { return false; }
                });
            if (bar != null) return new SeekBar(automation, bar, w);
        }
        catch (Exception) { }
        automation.Dispose();
        return null;
    }

    /// <summary>
    /// Pull the progress dot back a few seconds, then put the song back where it would have been: a playful "look at me",
    /// never lost listening. Keeps its own UIA connection; dispose when done.
    /// </summary>
    public sealed class SeekBar : IDisposable
    {
        private readonly UIA3Automation _automation;
        private readonly FlaUI.Core.AutomationElements.AutomationElement _el;
        private readonly Window _w;
        private double _back;
        private bool _byKeys;

        internal SeekBar(UIA3Automation automation, FlaUI.Core.AutomationElements.AutomationElement el, Window w) { _automation = automation; _el = el; _w = w; }

        /// <summary>
        /// The player's own seek keys, where its progress slider can't be set reliably (YouTube's reports a stale value and
        /// ignores SetValue): YouTube ←/→ = 5 s, Spotify Shift+←/→. Null = set the slider directly.
        /// </summary>
        public static (VirtualKeyShort? Mod, VirtualKeyShort Back, VirtualKeyShort Fwd)? SeekKeys(string process, string title) =>
            IsChromium(process) && title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) ? (null, VirtualKeyShort.LEFT, VirtualKeyShort.RIGHT)
            : process.Equals("spotify", StringComparison.OrdinalIgnoreCase) ? (VirtualKeyShort.SHIFT, VirtualKeyShort.LEFT, VirtualKeyShort.RIGHT)
            : null;

        private bool Press(bool back)
        {
            if (SeekKeys(_w.Process, _w.Title) is not { } k || NativeMethods.Foreground() != _w.Hwnd) return false;
            try
            {
                // arrows in a text box would move the caret, not the video: only when no edit field has focus
                var focused = _automation.FocusedElement();
                if (focused?.Properties.ControlType.ValueOrDefault is ControlType.Edit or ControlType.ComboBox or ControlType.Document && focused.Patterns.Value.IsSupported && !focused.Patterns.Value.Pattern.IsReadOnly.ValueOrDefault)
                    return false;
            }
            catch (Exception) { }
            var key = back ? k.Back : k.Fwd;
            if (k.Mod is { } m) Keyboard.TypeSimultaneously(m, key); else Keyboard.Type(key);
            return true;
        }

        public static bool LooksLikeProgress(string name) =>
            !name.Contains("volume") && !name.Contains("âm lượng") &&
            new[] { "seek", "position", "progress", "playback", "timeline", "tiến trình", "vị trí" }.Any(name.Contains);

        /// <summary>How far one pull goes: ~3 s for a range in seconds; ~1.5% for a 0..1 range (~3 s of a 3-4 minute song).</summary>
        public static double BackStep(double min, double max) => max - min <= 1.0001 ? 0.015 * (max - min) : 3;

        /// <summary>
        /// Not at the very start, and not near the end: a live stream sits at its live edge (seeking there jumps to "live"
        /// on YouTube, skipping minutes), and a song about to end could roll over to the next track.
        /// </summary>
        public static bool SafeToPull(double v, double min, double max)
        {
            double range = max - min, step = BackStep(min, max);
            double endMargin = range <= 1.0001 ? 0.05 * range : 15;   // 5% of a song, or 15 s
            return v - min > step && max - v > endMargin;
        }

        /// <summary>Where the dot is on screen right now (physical pixels).</summary>
        public Point Dot()
        {
            var r = _el.BoundingRectangle;
            var rv = _el.Patterns.RangeValue.Pattern;
            double t = (rv.Value.Value - rv.Minimum.Value) / (rv.Maximum.Value - rv.Minimum.Value);
            return new Point(r.Left + (int)(r.Width * Math.Clamp(t, 0, 1)), r.Top + r.Height / 2);
        }

        public bool PullBack()
        {
            if (SeekKeys(_w.Process, _w.Title) != null) return _byKeys = Press(back: true);
            try
            {
                var rv = _el.Patterns.RangeValue.Pattern;
                double v = rv.Value.Value, min = rv.Minimum.Value, max = rv.Maximum.Value;
                if (!SafeToPull(v, min, max)) return false;
                _back = Math.Min(BackStep(min, max), v - min);
                rv.SetValue(v - _back);
                return Math.Abs(rv.Value.Value - v) > _back / 2;   // some players accept SetValue but ignore it: then nothing happened
            }
            catch (Exception) { return false; }
        }

        /// <summary>Back to where it would be without her: current position (which kept playing) plus what she pulled.</summary>
        public bool Restore()
        {
            if (_byKeys) return Press(back: false);
            try
            {
                var rv = _el.Patterns.RangeValue.Pattern;
                rv.SetValue(Math.Min(rv.Value.Value + _back, rv.Maximum.Value));
                return true;
            }
            catch (Exception) { return false; }
        }

        public void Dispose() => _automation.Dispose();
    }

    /// <summary>One line in actions.log.</summary>
    public static void Log(string what) => ActionLog.Note(what);

    private static Window? Describe(IntPtr h, uint ownPid)
    {
        if (!NativeMethods.IsAlive(h) || IsIconic(h) || !NativeMethods.IsAppWindow(h, requireTitle: true)) return null;
        uint pid = NativeMethods.ProcessIdOf(h);
        if (pid == 0 || pid == ownPid || NativeMethods.IsBlockedByElevation(h)) return null;
        var r = NativeMethods.WindowRect(h);
        if (r.Width < 100 || r.Height < 60) return null;
        return new Window(h, NativeMethods.ProcessNameOf(pid), NativeMethods.TitleOf(h), r);
    }

    private static bool IsFullscreen(IntPtr hwnd, Rectangle r)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* NEAREST */), ref info)) return true;   // unknown: be safe
        var m = info.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    private static string FriendlyName(Window w)
    {
        try
        {
            using var p = Process.GetProcessById((int)NativeMethods.ProcessIdOf(w.Hwnd));
            var d = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch (Exception) { }
        return w.Process;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);
}
