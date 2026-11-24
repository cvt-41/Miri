using Miri.Core;

namespace Miri.Hands;

/// <summary>Appends one line per executed action to DataDir\actions.log. Never throws.</summary>
internal static class ActionLog
{
    private static readonly object Lock = new();

    public static void Append(UiSnapshot? snapshot, UiAction action, ActionResult result)
    {
        try
        {
            var window = snapshot == null ? "-" : $"\"{Clean(snapshot.WindowTitle, 80)}\" ({snapshot.ProcessName})";
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {window} | {Describe(action, snapshot)} | " +
                       $"{(result.Success ? "OK" : "FAIL")}: {Clean(result.Message, 200)}{Environment.NewLine}";
            var path = Path.Combine(MiriSettings.DataDir, "actions.log");
            lock (Lock) File.AppendAllText(path, line);
        }
        catch (Exception)
        {
            // logging must never break an action
        }
    }

    /// <summary>A free-form line (e.g. the kitten's own mischief), same format.</summary>
    public static void Note(string what)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | - | {Clean(what, 200)} | OK{Environment.NewLine}";
            lock (Lock) File.AppendAllText(Path.Combine(MiriSettings.DataDir, "actions.log"), line);
        }
        catch (Exception)
        {
            // logging must never break an action
        }
    }

    internal static string Describe(UiAction action, UiSnapshot? snapshot)
    {
        var target = "";
        if (action.ElementId is int id)
        {
            var el = snapshot?.Elements.FirstOrDefault(e => e.Id == id);
            target = el == null ? $" #{id}" : $" #{id} {el.ControlType} \"{Clean(el.Name, 40)}\"";
        }
        return action.Kind switch
        {
            ActionKind.TypeText => $"TypeText{target} text=\"{Clean(action.Text, 60)}\"",
            ActionKind.PressKeys => $"PressKeys{target} keys={action.Keys}",
            ActionKind.Scroll => $"Scroll{target} amount={action.ScrollAmount}",
            ActionKind.OpenApp => $"OpenApp \"{Clean(action.Target, 60)}\"",
            ActionKind.FocusWindow => $"FocusWindow \"{Clean(action.Target, 60)}\"",
            _ => $"{action.Kind}{target}",
        };
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        return s.Length > max ? s[..max] + "..." : s;
    }
}
