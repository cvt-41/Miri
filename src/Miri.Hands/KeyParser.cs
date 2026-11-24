using FlaUI.Core.WindowsAPI;

namespace Miri.Hands;

/// <summary>
/// Parses key strings like "ctrl+s", "Enter", "alt+F4", "ctrl+shift+t" or sequences "ctrl+a ctrl+c".
/// Chords are separated by spaces or commas; keys inside a chord by '+'.
/// </summary>
internal static class KeyParser
{
    internal sealed record Chord(IReadOnlyList<string> Names, IReadOnlyList<VirtualKeyShort> Keys)
    {
        public string Canonical => string.Join("+", Names);
    }

    private static readonly Dictionary<string, (string Name, ushort Vk)> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = ("ctrl", 0x11), ["control"] = ("ctrl", 0x11), ["ctl"] = ("ctrl", 0x11),
        ["shift"] = ("shift", 0x10),
        ["alt"] = ("alt", 0x12), ["menu"] = ("alt", 0x12),
        ["win"] = ("win", 0x5B), ["windows"] = ("win", 0x5B), ["meta"] = ("win", 0x5B), ["cmd"] = ("win", 0x5B), ["super"] = ("win", 0x5B),
        ["enter"] = ("enter", 0x0D), ["return"] = ("enter", 0x0D),
        ["esc"] = ("esc", 0x1B), ["escape"] = ("esc", 0x1B),
        ["tab"] = ("tab", 0x09),
        ["space"] = ("space", 0x20), ["spacebar"] = ("space", 0x20),
        ["backspace"] = ("backspace", 0x08), ["back"] = ("backspace", 0x08), ["bksp"] = ("backspace", 0x08),
        ["delete"] = ("delete", 0x2E), ["del"] = ("delete", 0x2E),
        ["insert"] = ("insert", 0x2D), ["ins"] = ("insert", 0x2D),
        ["home"] = ("home", 0x24), ["end"] = ("end", 0x23),
        ["pageup"] = ("pageup", 0x21), ["pgup"] = ("pageup", 0x21),
        ["pagedown"] = ("pagedown", 0x22), ["pgdn"] = ("pagedown", 0x22),
        ["up"] = ("up", 0x26), ["arrowup"] = ("up", 0x26),
        ["down"] = ("down", 0x28), ["arrowdown"] = ("down", 0x28),
        ["left"] = ("left", 0x25), ["arrowleft"] = ("left", 0x25),
        ["right"] = ("right", 0x27), ["arrowright"] = ("right", 0x27),
        ["capslock"] = ("capslock", 0x14), ["printscreen"] = ("printscreen", 0x2C), ["prtsc"] = ("printscreen", 0x2C),
        ["plus"] = ("plus", 0xBB), ["minus"] = ("minus", 0xBD),
        ["comma"] = (",", 0xBC), ["period"] = (".", 0xBE), ["dot"] = (".", 0xBE),
        ["-"] = ("minus", 0xBD), ["="] = ("plus", 0xBB), [","] = (",", 0xBC), ["."] = (".", 0xBE),
        ["/"] = ("/", 0xBF), [";"] = (";", 0xBA), ["'"] = ("'", 0xDE), ["["] = ("[", 0xDB), ["]"] = ("]", 0xDD),
        ["\\"] = ("\\", 0xDC), ["`"] = ("`", 0xC0),
        // media / volume keys (global: Windows routes them to the active media session, e.g. Sonora, Spotify)
        ["playpause"] = ("playpause", 0xB3), ["mediaplaypause"] = ("playpause", 0xB3), ["play/pause"] = ("playpause", 0xB3),
        ["nexttrack"] = ("nexttrack", 0xB0), ["medianext"] = ("nexttrack", 0xB0),
        ["prevtrack"] = ("prevtrack", 0xB1), ["mediaprev"] = ("prevtrack", 0xB1), ["previoustrack"] = ("prevtrack", 0xB1),
        ["mediastop"] = ("mediastop", 0xB2),
        ["volumeup"] = ("volumeup", 0xAF), ["volumedown"] = ("volumedown", 0xAE), ["volumemute"] = ("volumemute", 0xAD), ["mute"] = ("volumemute", 0xAD),
    };

    private static readonly HashSet<ushort> GlobalMediaKeys = new() { 0xB3, 0xB0, 0xB1, 0xB2, 0xAF, 0xAE, 0xAD };

    /// <summary>True when every key is a media/volume key, which Windows delivers globally (no window focus needed).</summary>
    public static bool IsGlobalMediaOnly(string? keys) =>
        TryParse(keys, out var chords, out _) && chords.Count > 0 &&
        chords.All(c => c.Keys.Count == 1 && GlobalMediaKeys.Contains((ushort)c.Keys[0]));

    private static readonly string[] ModifierOrder = { "ctrl", "shift", "alt", "win" };

    /// <summary>Parses keys; returns false with an error message when something is unknown.</summary>
    public static bool TryParse(string? keys, out List<Chord> chords, out string error)
    {
        chords = new List<Chord>();
        error = "";
        if (string.IsNullOrWhiteSpace(keys))
        {
            error = "No keys given.";
            return false;
        }

        foreach (var chordText in keys.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var names = new List<string>();
            var vks = new List<VirtualKeyShort>();
            foreach (var raw in chordText.Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!TryKey(raw.Trim(), out var name, out var vk))
                {
                    error = $"I don't know the key \"{raw}\".";
                    return false;
                }
                if (names.Contains(name)) continue;
                names.Add(name);
                vks.Add(vk);
            }
            if (names.Count == 0) continue;

            // modifiers first (pressed first, released last)
            var order = names.Select((n, i) => (n, vk: vks[i], rank: Array.IndexOf(ModifierOrder, n) is var r && r >= 0 ? r : 10 + i))
                .OrderBy(t => t.rank).ToList();
            chords.Add(new Chord(order.Select(t => t.n).ToList(), order.Select(t => t.vk).ToList()));
        }

        if (chords.Count == 0)
        {
            error = "No keys given.";
            return false;
        }
        return true;
    }

    private static bool TryKey(string token, out string name, out VirtualKeyShort vk)
    {
        name = "";
        vk = 0;
        if (token.Length == 0) return false;
        if (Named.TryGetValue(token, out var n))
        {
            name = n.Name;
            vk = (VirtualKeyShort)n.Vk;
            return true;
        }
        var lower = token.ToLowerInvariant();
        if (lower.Length == 1 && lower[0] is >= 'a' and <= 'z')
        {
            name = lower;
            vk = (VirtualKeyShort)(ushort)char.ToUpperInvariant(lower[0]);
            return true;
        }
        if (lower.Length == 1 && lower[0] is >= '0' and <= '9')
        {
            name = lower;
            vk = (VirtualKeyShort)(ushort)lower[0];
            return true;
        }
        if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower[1..], out var f) && f is >= 1 and <= 24)
        {
            name = lower;
            vk = (VirtualKeyShort)(ushort)(0x70 + f - 1);
            return true;
        }
        return false;
    }
}
