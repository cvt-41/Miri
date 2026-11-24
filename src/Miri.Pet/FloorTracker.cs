using System.Diagnostics;
using System.Windows;
using static Miri.Pet.NativeMethods;

namespace Miri.Pet;

/// <summary>
/// A horizontal surface the kitten can stand on (DIPs). Hwnd == 0 is the taskbar edge.
/// Key 0 = a window's title bar; Key &gt; 0 = a ledge inside an app (e.g. the bottom of a video player).
/// </summary>
public readonly record struct Floor(IntPtr Hwnd, double Y, double Left, double Right, long Key = 0)
{
    public bool IsLedge => Key != 0;

    public bool Covers(double x) => x >= Left && x <= Right;
}

/// <summary>
/// Collects floors: the top edge of the work area (= top of the taskbar) plus the visible part of the
/// top edge of every visible top-level window title bar, ordered by Z (occluded spans removed).
/// </summary>
internal sealed class FloorTracker
{
    private static readonly HashSet<string> SkipClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public IReadOnlyList<Floor> Floors { get; private set; } = Array.Empty<Floor>();

    // ---- content ledge (one at a time): set when the kitten is dropped on something like a video player
    private Miri.Core.ISurface? _ledge;
    private long _ledgeKey;
    private Rect? _ledgeRectDip;          // updated by the background poll
    private CancellationTokenSource? _ledgeCts;

    public bool HasLedge => _ledge != null;

    public void SetLedge(Miri.Core.ISurface surface)
    {
        ClearLedge();
        _ledge = surface;
        _ledgeKey = DateTime.UtcNow.Ticks;
        PollLedgeOnce();
        var cts = _ledgeCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(250, cts.Token).ConfigureAwait(false); } catch { break; }
                PollLedgeOnce();
            }
        });
    }

    public void ClearLedge()
    {
        _ledgeCts?.Cancel();
        _ledgeCts = null;
        _ledge = null;
        _ledgeRectDip = null;
    }

    /// <summary>Reads the surface's live bounds and checks it is not hidden behind another window (any thread).</summary>
    private void PollLedgeOnce()
    {
        var s = _ledge;
        if (s == null) return;
        var b = s.CurrentBounds();
        if (b is not { } r || !Visible(s.Hwnd, r))
        {
            _ledgeRectDip = null;
            return;
        }
        double k = ScreenMetrics.Scale;
        _ledgeRectDip = new Rect(r.Left / k, r.Top / k, r.Width / k, r.Height / k);
    }

    /// <summary>Sample points just above the ledge must mostly belong to the ledge's window (Miri's own windows are ignored).</summary>
    private bool Visible(IntPtr owner, System.Drawing.Rectangle r)
    {
        int y = _ledge?.TopEdge == true ? r.Top + 6 : r.Bottom - 6, mine = 0, other = 0;
        foreach (double f in new[] { 0.15, 0.5, 0.85 })
        {
            int x = r.Left + (int)(r.Width * f);
            var h = WindowFromPoint(new POINT { X = x, Y = y });
            var root = h == IntPtr.Zero ? IntPtr.Zero : GetAncestor(h, GA_ROOT);
            if (root == owner) { mine++; continue; }
            GetWindowThreadProcessId(root, out var pid);
            if (pid == _ownPid) continue;   // the kitten / bubble itself
            other++;
        }
        return mine > 0 && other <= 1;
    }
    public Rect WorkArea { get; private set; }

    /// <param name="petHeight">Kitten height in DIPs (floors without head room are skipped).</param>
    public void Refresh(double petHeight)
    {
        var work = ScreenMetrics.WorkAreaDip();
        WorkArea = work;
        var floors = new List<Floor> { new(IntPtr.Zero, work.Bottom, work.Left, work.Right) };
        var above = new List<Rect>();   // rects of windows higher in Z order

        try
        {
            EnumWindows((h, _) =>
            {
                try { Consider(h, work, petHeight, floors, above); } catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { /* keep at least the taskbar floor */ }

        // the content ledge goes first so it wins over a title bar at the same spot
        if (_ledge != null && _ledgeRectDip is { } lr)
        {
            double l = Math.Max(lr.Left + 4, work.Left), rr = Math.Min(lr.Right - 4, work.Right);
            double y = _ledge.TopEdge ? lr.Top : lr.Bottom;
            if (rr - l >= 40 && y - petHeight >= work.Top - 1)
                floors.Insert(0, new Floor(_ledge.Hwnd, Math.Round(y), l, rr, _ledgeKey));
        }

        Floors = floors;
    }

    private void Consider(IntPtr h, Rect work, double petHeight, List<Floor> floors, List<Rect> above)
    {
        if (!IsWindowVisible(h) || IsIconic(h)) return;
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return;
        GetWindowThreadProcessId(h, out var pid);
        if (pid == _ownPid) return;

        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(h, out r);
        if (r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) return;
        var rect = new Rect(r.Left / ScreenMetrics.Scale, r.Top / ScreenMetrics.Scale,
                            (r.Right - r.Left) / ScreenMetrics.Scale, (r.Bottom - r.Top) / ScreenMetrics.Scale);

        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        bool isFloorCandidate =
            (style & WS_CAPTION) == WS_CAPTION &&
            (ex & WS_EX_TOOLWINDOW) == 0 &&
            GetWindowTextLength(h) > 0 &&
            !IsZoomed(h) &&
            !SkipClasses.Contains(ClassName(h)) &&
            rect.Width >= 160;

        if (isFloorCandidate)
        {
            double y = Math.Round(rect.Top);
            if (y - petHeight >= work.Top && y < work.Bottom - 8)
            {
                var spans = new List<(double L, double R)> { (Math.Max(rect.Left + 6, work.Left), Math.Min(rect.Right - 6, work.Right)) };
                foreach (var a in above)
                {
                    // a window above hides the edge if it covers the strip just above / at the edge
                    if (a.Top < y + 2 && a.Bottom > y - 6)
                        spans = Subtract(spans, a.Left, a.Right);
                    if (spans.Count == 0) break;
                }
                foreach (var (l, rr) in spans)
                    if (rr - l >= 40) floors.Add(new Floor(h, y, l, rr));
            }
        }

        above.Add(rect);
    }

    private static List<(double L, double R)> Subtract(List<(double L, double R)> spans, double l, double r)
    {
        var res = new List<(double, double)>();
        foreach (var (a, b) in spans)
        {
            if (r <= a || l >= b) { res.Add((a, b)); continue; }
            if (l > a) res.Add((a, l));
            if (r < b) res.Add((r, b));
        }
        return res;
    }

    [Conditional("DEBUG")]
    public void Dump() => Debug.WriteLine(string.Join("\n", Floors));
}
