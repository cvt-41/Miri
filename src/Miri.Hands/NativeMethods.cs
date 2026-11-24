using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Miri.Hands;

/// <summary>Win32 helpers used by the screen reader and the executor.</summary>
internal static class NativeMethods
{
    private const uint GW_HWNDNEXT = 2;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint INPUT_MOUSE = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;   // MOUSEINPUT is the largest union member, so sizes match the native INPUT
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
    };

    public static IntPtr Foreground() => GetForegroundWindow();
    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    public static string TitleOf(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ProcessNameOf(uint pid)
    {
        if (pid == 0) return "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static System.Drawing.Rectangle WindowRect(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return System.Drawing.Rectangle.Empty;
        return System.Drawing.Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>A "real" app window a user would think of as a window.</summary>
    public static bool IsAppWindow(IntPtr hwnd, bool requireTitle)
    {
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd)) return false;
        if (ShellClasses.Contains(ClassOf(hwnd))) return false;
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if (requireTitle && GetWindowTextLength(hwnd) == 0) return false;
        return true;
    }

    /// <summary>Top-level windows in Z-order (topmost first).</summary>
    public static IEnumerable<IntPtr> ZOrderFrom(IntPtr start)
    {
        var h = start == IntPtr.Zero ? GetTopWindow(IntPtr.Zero) : GetWindow(start, GW_HWNDNEXT);
        var guard = 0;
        while (h != IntPtr.Zero && guard++ < 2000)
        {
            yield return h;
            h = GetWindow(h, GW_HWNDNEXT);
        }
    }

    public static List<IntPtr> TopLevelWindows()
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) => { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Best-effort foreground switch. Returns true if a window of the target's process is now foreground.</summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (!IsAlive(hwnd)) return false;
        var targetPid = ProcessIdOf(hwnd);
        if (ProcessIdOf(GetForegroundWindow()) == targetPid && !IsIconic(hwnd)) return true;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        // A zero-length mouse move makes us "the last input source", which relaxes the foreground lock.
        try
        {
            var inputs = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } } };
            SendInput(1, inputs, Marshal.SizeOf<INPUT>());
        }
        catch (Exception) { /* ignore */ }

        var fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var myThread = GetCurrentThreadId();
        var attached = fgThread != 0 && fgThread != myThread && AttachThreadInput(myThread, fgThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(myThread, fgThread, false);
        }

        for (var i = 0; i < 10; i++)
        {
            if (ProcessIdOf(GetForegroundWindow()) == targetPid) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static bool? _selfElevated;

    public static bool SelfElevated => _selfElevated ??= IsProcessElevated((uint)Environment.ProcessId) == true;

    /// <summary>true = elevated, false = not, null = unknown. Access denied is treated as elevated.</summary>
    public static bool? IsProcessElevated(uint pid)
    {
        if (pid == 0) return null;
        var hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? true : null;
        try
        {
            if (!OpenProcessToken(hProc, TOKEN_QUERY, out var token))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED ? true : null;
            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _)
                    ? elevated != 0
                    : null;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(hProc); }
    }

    /// <summary>True when the target window runs elevated and we don't (UIPI blocks us).</summary>
    public static bool IsBlockedByElevation(IntPtr hwnd)
    {
        if (SelfElevated) return false;
        return IsProcessElevated(ProcessIdOf(hwnd)) == true;
    }
}
