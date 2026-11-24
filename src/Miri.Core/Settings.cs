using System.Text.Json;

namespace Miri.Core;

public sealed class MiriSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string ChatModel { get; set; } = "qwen3.5:4b";
    public string AlternateModel { get; set; } = "gemma4:e4b";
    public string FallbackModel { get; set; } = "qwen3:4b";
    /// <summary>Model used whenever a request includes an image (screenshots). Empty = use the normal chain.</summary>
    public string VisionModel { get; set; } = "gemma4:e4b";
    /// <summary>
    /// How long Ollama keeps a model in RAM after Miri's last request (Ollama duration, e.g. "15s", "5m").
    /// Short = models load only when you ask something and free their RAM right after. Vision models always unload at once.
    /// </summary>
    public string ModelKeepAlive { get; set; } = "15s";
    public int MaxAgentSteps { get; set; } = 8;
    public List<string> BlockedApps { get; set; } = new() { "KeePass", "Bitwarden", "1Password" };
    public const string DefaultChatHotkey = "Ctrl+Alt+K";
    /// <summary>Old default; clashed with other apps' quick-entry hotkeys (e.g. the Claude desktop app).</summary>
    private const string LegacyChatHotkey = "Ctrl+Alt+Space";

    public string ChatHotkey { get; set; } = DefaultChatHotkey;
    public string StopHotkey { get; set; } = "Ctrl+Alt+M";
    /// <summary>Music app used for "play &lt;song&gt;" when the user doesn't name one.</summary>
    public string MusicApp { get; set; } = "Sonora";
    public int Scale { get; set; } = 3;           // sprite pixel scale
    /// <summary>Now and then she teases you: switches the browser tab or the app in front (never types or clicks content).</summary>
    public bool Mischief { get; set; } = true;
    /// <summary>"What's the weather?" uses Windows location (rounded to ~10 km, sent to Open-Meteo). Off = only cities you name or WeatherCity.</summary>
    public bool WeatherUseLocation { get; set; } = true;
    /// <summary>False until the first-start model picker was answered.</summary>
    public bool ModelSetupDone { get; set; }
    /// <summary>Look for a newer Miri on GitHub once a day at start (reads the public release list only).</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>City used for weather when location is off or unavailable (e.g. "Hanoi").</summary>
    public string WeatherCity { get; set; } = "";

    /// <summary>%LOCALAPPDATA%\Miri, created on access.</summary>
    public static string DataDir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Miri");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>Loads DataDir\settings.json; returns defaults if missing or corrupt.</summary>
    public static MiriSettings Load()
    {
        try
        {
            var path = SettingsPath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<MiriSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    loaded.BlockedApps ??= new();
                    if (string.IsNullOrWhiteSpace(loaded.ChatHotkey) ||
                        string.Equals(loaded.ChatHotkey.Replace(" ", ""), LegacyChatHotkey, StringComparison.OrdinalIgnoreCase))
                    {
                        loaded.ChatHotkey = DefaultChatHotkey;   // migrate the old, clashing default
                        try { loaded.Save(); } catch (Exception) { /* read-only file: the in-memory value still applies */ }
                    }
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // corrupt or unreadable: fall back to defaults
        }
        return new MiriSettings();
    }

    public void Save()
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
