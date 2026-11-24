using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Miri.Core;

namespace Miri.Hands;

/// <summary>
/// Finds a "ledge" under a point with UI Automation: the smallest reasonably large element that contains
/// the point and whose bottom edge is visible (a video player, an image, a panel). Never Miri's own windows.
/// </summary>
public sealed class UiaSurfaceFinder : ISurfaceFinder, IDisposable
{
    private const int MinHeight = 60;        // bottom-edge ledges: panels, players
    private const int MinTopHeight = 24;     // top-edge ledges: toolbars, cards, text boxes
    private static readonly int[] ProbeDown = { 0, 12, 28, 48 };   // physical px below the paws, so a drop just above an edge counts
    private readonly UIA3Automation _automation = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public ISurface? FindAt(Point paws, int minWidth)
    {
        foreach (int dy in ProbeDown)
            if (FindAt(paws, new Point(paws.X, paws.Y + dy), minWidth) is { } s) return s;
        return null;
    }

    private ISurface? FindAt(Point paws, Point p, int minWidth)
    {
        try
        {
            var top = RootWindowAt(p);
            if (top == IntPtr.Zero) return null;
            GetWindowThreadProcessId(top, out var pid);
            if (pid == _ownPid) return null;
            if (!GetWindowRect(top, out var wr)) return null;
            var win = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
            long winArea = (long)win.Width * win.Height;

            AutomationElement? el = _automation.FromPoint(p);
            var walker = _automation.TreeWalkerFactory.GetControlViewWalker();
            for (int depth = 0; el != null && depth < 40; depth++)
            {
                Rectangle r;
                try { r = el.BoundingRectangle; } catch { r = Rectangle.Empty; }
                bool ok = !r.IsEmpty && r.Contains(p) && r.Width >= minWidth &&
                          (long)r.Width * r.Height < winArea * 0.85;          // not the whole window
                // top edge just under the paws and inside the window: stand on it
                if (ok && r.Height >= MinTopHeight && r.Top >= paws.Y - 6 && r.Top <= paws.Y + ProbeDown[^1] + 4 && r.Top >= win.Top + 2)
                {
                    string tn;
                    try { tn = el.Properties.Name.ValueOrDefault ?? ""; } catch { tn = ""; }
                    return new UiaSurface(el, top, tn, topEdge: true);
                }
                if (ok && r.Height >= MinHeight &&
                    r.Bottom > p.Y + 4 &&                              // the ledge is below the paws
                    r.Bottom <= win.Bottom + 2)                              // bottom edge inside the window
                {
                    string name;
                    try { name = el.Properties.Name.ValueOrDefault ?? ""; } catch { name = ""; }
                    return new UiaSurface(el, top, name);
                }
                try { el = walker.GetParent(el); } catch { el = null; }
            }
        }
        catch (Exception)
        {
            // UIA can fail on elevated / dying windows: just no ledge.
        }
        return null;
    }

    public void Dispose() => _automation.Dispose();

    private static IntPtr RootWindowAt(Point p)
    {
        var h = WindowFromPoint(new POINT { X = p.X, Y = p.Y });
        return h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
    }

    private sealed class UiaSurface : ISurface
    {
        private readonly AutomationElement _el;

        public UiaSurface(AutomationElement el, IntPtr hwnd, string name, bool topEdge = false)
        {
            TopEdge = topEdge;
            _el = el;
            Hwnd = hwnd;
            Name = name;
        }

        public IntPtr Hwnd { get; }
        public string Name { get; }
        public bool TopEdge { get; }

        public Rectangle? CurrentBounds()
        {
            try
            {
                if (!IsWindow(Hwnd) || !IsWindowVisible(Hwnd) || IsIconic(Hwnd)) return null;
                if (_el.Properties.IsOffscreen.ValueOrDefault) return null;
                var r = _el.BoundingRectangle;
                return r.IsEmpty || r.Width < 40 || r.Height < 40 ? null : r;
            }
            catch (Exception)
            {
                return null;   // element destroyed (page navigated, tab closed)
            }
        }
    }

    // ---------------------------------------------------------------- native

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
}
