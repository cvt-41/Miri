using System.Windows.Input;
using System.Windows.Interop;

namespace Miri.App;

/// <summary>Global hotkeys via RegisterHotKey on one of our windows.</summary>
internal sealed class HotkeyManager : IDisposable
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0xB001;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);
    }

    /// <summary>Registers e.g. "Ctrl+Alt+K". Returns false with a reason when the combo is invalid or taken.</summary>
    public bool Register(string combo, Action action, out string error)
    {
        error = "";
        if (!TryParse(combo, out uint mods, out uint vk))
        {
            error = $"Can't understand hotkey \"{combo}\" (use e.g. Ctrl+Alt+K).";
            return false;
        }
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            error = $"{combo} is already used by another app.";
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; continue;
                case "alt": mods |= MOD_ALT; continue;
                case "shift": mods |= MOD_SHIFT; continue;
                case "win": case "windows": case "meta": mods |= MOD_WIN; continue;
            }
            string name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
            if (name.Equals("esc", StringComparison.OrdinalIgnoreCase)) name = "Escape";
            if (name.Equals("enter", StringComparison.OrdinalIgnoreCase)) name = "Return";
            if (!Enum.TryParse<Key>(name, true, out var key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        }
        return vk != 0;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys) Win32.UnregisterHotKey(_hwnd, id);
        _actions.Clear();
        _source?.RemoveHook(WndProc);
    }
}
