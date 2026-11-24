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
