using System.Runtime.InteropServices;
using System.Text;

namespace Miri.Pet;

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr CreateCursor(IntPtr hInst, int xHot, int yHot, int w, int h, byte[] and, byte[] xor);
    [DllImport("user32.dll")] private static extern bool SetSystemCursor(IntPtr cursor, uint id);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);

    private static readonly uint[] PointerIds = { 32512, 32513, 32649, 32515, 32650, 32514 };   // arrow, I-beam, hand, cross, app-starting, wait
    public static bool PointerHidden { get; private set; }

    /// <summary>Hides the mouse pointer everywhere by swapping in blank system cursors (restored by <see cref="ShowPointer"/>).</summary>
    public static void HidePointer()
    {
        if (PointerHidden) return;
        PointerHidden = true;
        foreach (var id in PointerIds)   // SetSystemCursor takes ownership, so each id gets its own blank cursor
            SetSystemCursor(CreateCursor(IntPtr.Zero, 0, 0, 32, 32, Enumerable.Repeat((byte)0xFF, 128).ToArray(), new byte[128]), id);
    }

    /// <summary>Reloads the user's own pointer scheme. Safe to call any time (also at start and exit, in case of a crash mid-hug).</summary>
    public static void ShowPointer()
    {
        PointerHidden = false;
        SystemParametersInfo(0x0057 /* SPI_SETCURSORS */, 0, IntPtr.Zero, 0);
    }
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, out RECT r, uint winIni);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);

    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_CAPTION = 0x00C00000L, WS_EX_TOOLWINDOW = 0x00000080L, WS_EX_NOACTIVATE = 0x08000000L;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    public const uint SPI_GETWORKAREA = 0x0030;

    /// <summary>Milliseconds since the last keyboard/mouse input anywhere in the session.</summary>
    public static uint IdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return unchecked((uint)Environment.TickCount - info.dwTime);
    }

    public static string ClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
