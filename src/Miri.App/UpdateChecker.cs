using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace Miri.App;

/// <summary>
/// Asks GitHub whether a newer Miri release exists (tags like "v0.4.0"; other tags are ignored). Only reads the public
/// release list: sends nothing about the user. Downloads the release's Miri-Setup.exe only after the user says yes.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "cvt-41/Miri";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    static UpdateChecker() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("Miri-desktop-pet");   // GitHub requires a User-Agent

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public sealed record Release(Version Version, string Url, string? SetupUrl = null);

    /// <summary>The newest published release newer than this build, or null when up to date.</summary>
    public static async Task<Release?> NewerReleaseAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=30", ct));
        var published = doc.RootElement.EnumerateArray()
            .Where(r => !r.GetProperty("draft").GetBoolean() && !r.GetProperty("prerelease").GetBoolean()).ToList();
        var best = Newest(published.Select(r => (r.GetProperty("tag_name").GetString() ?? "", r.GetProperty("html_url").GetString() ?? "")), Current);
        if (best == null) return null;
        var setup = published.First(r => r.GetProperty("html_url").GetString() == best.Url).GetProperty("assets").EnumerateArray()
            .Where(a => a.GetProperty("name").GetString() == "Miri-Setup.exe")
            .Select(a => a.GetProperty("browser_download_url").GetString()).FirstOrDefault(IsOurDownload);
        return best with { SetupUrl = setup };
    }

    /// <summary>Only a file attached to one of our own releases on GitHub is ever downloaded.</summary>
    public static bool IsOurDownload(string? url) =>
        url != null && url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase) && !url.Contains("..");

    /// <summary>Downloads the release's Miri-Setup.exe to %TEMP% and returns its path.</summary>
    public static async Task<string> DownloadSetupAsync(Release r, CancellationToken ct = default)
    {
        if (!IsOurDownload(r.SetupUrl)) throw new InvalidOperationException("Not a Miri release download: " + r.SetupUrl);
        var path = Path.Combine(Path.GetTempPath(), $"Miri-Setup-{r.Version}.exe");
        using var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        dl.DefaultRequestHeaders.UserAgent.ParseAdd("Miri-desktop-pet");
        using (var src = await dl.GetStreamAsync(r.SetupUrl, ct))
        using (var dst = File.Create(path))
            await src.CopyToAsync(dst, ct);
        return path;
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
