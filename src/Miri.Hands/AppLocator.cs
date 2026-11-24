using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Miri.Hands;

/// <summary>
/// Finds installed apps by a loose name ("zalo", "máy tính", "vs code") using the same list the Start menu
/// shows: the shell's AppsFolder (desktop shortcuts + Store/UWP apps), with a Start-menu .lnk scan as fallback.
/// </summary>
internal static class AppLocator
{
    internal sealed record AppEntry(string Name, string LaunchId, bool IsShellApp);

    private static readonly object Gate = new();
    private static List<AppEntry>? _cache;
    private static DateTime _cacheTime;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private static readonly Dictionary<string, string> Nicknames = new(StringComparer.Ordinal)
    {
        ["vs code"] = "visual studio code", ["vscode"] = "visual studio code",
        ["vs"] = "visual studio", ["ps"] = "photoshop", ["fb"] = "facebook", ["mess"] = "messenger",
        ["tele"] = "telegram", ["ytb"] = "youtube", ["yt"] = "youtube", ["gg chrome"] = "google chrome",
        ["camera"] = "camera", ["may anh"] = "camera", ["lich"] = "calendar", ["dong ho"] = "clock",
        ["ban do"] = "maps", ["anh"] = "photos", ["thoi tiet"] = "weather", ["cua hang"] = "microsoft store",
    };

    private static readonly string[] JunkWords =
    {
        "uninstall", "readme", "read me", "help", "documentation", "release notes", "website", "license",
        "gỡ cài đặt", "manual",
    };

    /// <summary>Pre-loads the app list in the background so the first "open X" is fast.</summary>
    public static void Warm() => Task.Run(() => { try { GetApps(); } catch { } });

    public static IReadOnlyList<AppEntry> GetApps()
    {
        lock (Gate)
        {
            if (_cache != null && DateTime.UtcNow - _cacheTime < CacheTtl) return _cache;
        }
        var list = LoadShellApps();
        if (list.Count == 0) list = LoadStartMenuShortcuts();
        list = list
            .Where(a => !JunkWords.Any(j => a.Name.Contains(j, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .ToList();
        lock (Gate)
        {
            _cache = list;
            _cacheTime = DateTime.UtcNow;
        }
        return list;
    }

    /// <summary>Best match for <paramref name="query"/>, or null. <paramref name="suggestions"/> gets near misses.</summary>
    public static AppEntry? Find(string query, out IReadOnlyList<string> suggestions)
    {
        suggestions = Array.Empty<string>();
        var q = Normalize(query);
        if (Nicknames.TryGetValue(q, out var full)) q = full;
        if (q.Length == 0) return null;
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var qCompact = q.Replace(" ", "");

        var scored = new List<(AppEntry App, int Score)>();
        foreach (var app in GetApps())
        {
            var n = Normalize(app.Name);
            var nCompact = n.Replace(" ", "");
            var nWords = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int score = 0;
            if (n == q || nCompact == qCompact) score = 100;
            else if (n.StartsWith(q + " ", StringComparison.Ordinal)) score = 85;   // "zalo" → "Zalo PC"
            else if (nWords.Any(w => w == q) || nWords.Any(w => w == qCompact)) score = 75; // "code" → "Visual Studio Code"
            else if (Acronym(nWords) == qCompact && qCompact.Length >= 2) score = 70;         // "vs code"→? / "vlc"
            else if (qWords.All(w => nWords.Any(nw => nw.StartsWith(w, StringComparison.Ordinal)))) score = 60;
            else if (nCompact.Contains(qCompact, StringComparison.Ordinal) && qCompact.Length >= 3) score = 50;
            else if (qCompact.Length >= 4 && Levenshtein(qCompact, nCompact) <= Math.Max(1, qCompact.Length / 4)) score = 55;   // typo ("spotfy"): good enough to open
            if (score > 0)
            {
                score -= Math.Min(15, Math.Max(0, n.Length - q.Length) / 3);   // prefer shorter names
                scored.Add((app, score));
            }
        }

        var ranked = scored.OrderByDescending(s => s.Score).ThenBy(s => s.App.Name.Length).ToList();
        if (ranked.Count > 0 && ranked[0].Score >= 45) return ranked[0].App;

        // near misses for a helpful "did you mean" message
        suggestions = GetApps()
            .Select(a => (a.Name, d: Levenshtein(qCompact, Normalize(a.Name).Replace(" ", ""))))
            .Where(t => t.d <= Math.Max(2, qCompact.Length / 2))
            .OrderBy(t => t.d).Take(3).Select(t => t.Name).ToList();
        return null;
    }

    public static void Launch(AppEntry app)
    {
        if (app.IsShellApp)
        {
            // Works for both desktop and Store apps: explorer.exe shell:AppsFolder\<AppUserModelID or path>
            using var p = Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.LaunchId)
            {
                UseShellExecute = false,
            });
        }
        else
        {
            using var p = Process.Start(new ProcessStartInfo(app.LaunchId) { UseShellExecute = true });
        }
    }

    // ------------------------------------------------------------------ loading

    private static List<AppEntry> LoadShellApps()
    {
        var result = new List<AppEntry>();
        // Shell COM objects want an STA thread.
        var t = new Thread(() =>
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                if (folder == null) return;
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string path = item.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path) &&
                        !path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        lock (result) result.Add(new AppEntry(name.Trim(), path.Trim(), true));
                }
            }
            catch
            {
                // fall back to the .lnk scan
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(TimeSpan.FromSeconds(8));
        lock (result) return result.ToList();
    }

    private static List<AppEntry> LoadStartMenuShortcuts()
    {
        var result = new List<AppEntry>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                 })
        {
            try
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                foreach (var f in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    result.Add(new AppEntry(Path.GetFileNameWithoutExtension(f), f, false));
            }
            catch
            {
                // unreadable folder: skip
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ text helpers

    internal static string Normalize(string s)
    {
        var d = s.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Acronym(string[] words) => new(words.Where(w => w.Length > 0).Select(w => w[0]).ToArray());

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev2 = new int[b.Length + 1];   // row i-2, for swapped neighbours ("discrod")
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) cur[j] = Math.Min(cur[j], prev2[j - 2] + 1);
            }
            (prev2, prev, cur) = (prev, cur, prev2);
        }
        return prev[b.Length];
    }
}
