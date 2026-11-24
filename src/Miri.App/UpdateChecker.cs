using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Miri.App;

/// <summary>
/// Asks GitHub whether a newer Miri release exists (tags like "v0.4.0"; other tags are ignored). Only reads the public
/// release list: sends nothing about the user. Never downloads or installs anything; the user opens the release page.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "cvt-41/Miri";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Miri-desktop-pet");   // GitHub requires a User-Agent

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public sealed record Release(Version Version, string Url);

    /// <summary>The newest published release newer than this build, or null when up to date.</summary>
    public static async Task<Release?> NewerReleaseAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30", ct));
        return Newest(doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean())
            .Select(r => (r.GetProperty("tag_name").GetString() ?? "", r.GetProperty("html_url").GetString() ?? "")), Current);
    }

    /// <summary>Pure part: highest version tag above <paramref name="current"/> (compares major.minor.build only).</summary>
    public static Release? Newest(IEnumerable<(string Tag, string Url)> releases, Version current)
    {
        Release? best = null;
        foreach (var (tag, url) in releases)
            if (Parse(tag) is { } v && v > Trim(current) && (best == null || v > best.Version)) best = new Release(v, url);
        return best;
    }

    private static Version? Parse(string tag) =>
        Version.TryParse(tag.Trim().TrimStart('v', 'V'), out var v) ? Trim(v) : null;   // "prototype" etc.: not a version

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}
