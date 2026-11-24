using Miri.Core;

namespace Miri.Brain;

internal sealed class NoModelAvailableException : Exception
{
    public NoModelAvailableException(bool needVision, IEnumerable<string> tried)
        : base((needVision ? "No vision-capable model installed. Tried: " : "No chat model installed. Tried: ") + string.Join(", ", tried))
    {
        NeedVision = needVision;
    }

    public bool NeedVision { get; }
}

/// <summary>
/// Wraps <see cref="OllamaClient"/> with the model fallback chain
/// ActiveModel → ChatModel → AlternateModel → FallbackModel, reporting switches via OnStatus.
/// </summary>
internal sealed class LlmService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly OllamaClient _client;
    private readonly MiriSettings _settings;
    private readonly IMiriEvents _events;
    private readonly Func<string> _activeModel;
    private readonly object _gate = new();

    private IReadOnlyList<OllamaModelInfo>? _cache;
    private DateTime _cacheTime;
    private string? _lastNotice;

    public LlmService(OllamaClient client, MiriSettings settings, IMiriEvents events, Func<string> activeModel)
    {
        _client = client;
        _settings = settings;
        _events = events;
        _activeModel = activeModel;
    }

    public OllamaClient Client => _client;

    public async Task<IReadOnlyList<OllamaModelInfo>> GetModelsAsync(CancellationToken ct, bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _cache != null && DateTime.UtcNow - _cacheTime < CacheTtl) return _cache;
        }
        var models = await _client.ListModelsAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _cache = models;
            _cacheTime = DateTime.UtcNow;
        }
        return models;
    }

    private void InvalidateCache()
    {
        lock (_gate) _cache = null;
    }

    // Vision requests try the dedicated vision model first (gemma4:e4b reads screenshots faster and better).
    private IEnumerable<string> Candidates(bool needVision = false) =>
        (needVision
            ? new[] { _settings.VisionModel, _activeModel(), _settings.ChatModel, _settings.AlternateModel, _settings.FallbackModel }
            : new[] { _activeModel(), _settings.ChatModel, _settings.AlternateModel, _settings.FallbackModel })
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static OllamaModelInfo? Find(IReadOnlyList<OllamaModelInfo> installed, string name)
    {
        static string Norm(string n) => n.EndsWith(":latest", StringComparison.OrdinalIgnoreCase) ? n[..^7] : n;
        var target = Norm(name);
        return installed.FirstOrDefault(m => string.Equals(Norm(m.Name), target, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SupportsVision(OllamaModelInfo m) =>
        m.Capabilities.Count == 0   // older Ollama: unknown, assume yes
        || m.Capabilities.Contains("vision", StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the ordered list of installed candidate models (vision-capable only if requested).</summary>
    public async Task<List<string>> ResolveCandidatesAsync(bool needVision, CancellationToken ct, bool refresh = false)
    {
        var installed = await GetModelsAsync(ct, refresh).ConfigureAwait(false);
        var result = new List<string>();
        foreach (var c in Candidates(needVision))
        {
            var m = Find(installed, c);
            if (m == null) continue;
            if (needVision && !SupportsVision(m)) continue;
            result.Add(m.Name);
        }
        return result;
    }

    private static OllamaChatOptions WithKeepAlive(OllamaChatOptions? o, string keepAlive) => new()
    {
        Temperature = o?.Temperature,
        NumPredict = o?.NumPredict,
        NumCtx = o?.NumCtx ?? OllamaChatOptions.SharedNumCtx,
        Think = o?.Think ?? false,
        Tools = o?.Tools,
        KeepAlive = keepAlive,
    };

    private async Task UnloadOthersAsync(string model, CancellationToken ct)
    {
        IReadOnlyList<string> loaded;
        try { loaded = await _client.ListLoadedAsync(ct).ConfigureAwait(false); }
        catch (Exception) { return; }
        foreach (var other in loaded)
            if (!string.Equals(other, model, StringComparison.OrdinalIgnoreCase))
                await _client.UnloadAsync(other, ct).ConfigureAwait(false);
    }

    private bool IsActive(string model)
    {
        var active = _activeModel();
        return string.Equals(model, active, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(model, active + ":latest", StringComparison.OrdinalIgnoreCase);
    }

    private void NoticeIfSwitched(string used)
    {
        var active = _activeModel();
        if (string.Equals(used, active, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(used, active + ":latest", StringComparison.OrdinalIgnoreCase))
        {
            _lastNotice = null;
            return;
        }
        if (string.Equals(_lastNotice, used, StringComparison.OrdinalIgnoreCase)) return;
        _lastNotice = used;
        _events.OnStatus($"{active} unavailable, using {used}");
    }

    /// <summary>Chat with the first available model in the fallback chain.</summary>
    public async Task<OllamaChatResult> ChatAsync(IReadOnlyList<OllamaMessage> messages, OllamaChatOptions? options,
        Action<string>? onToken, CancellationToken ct, bool needVision = false)
    {
        var tried = new List<string>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var candidates = await ResolveCandidatesAsync(needVision, ct, refresh: attempt > 0).ConfigureAwait(false);
            foreach (var model in candidates)
            {
                if (tried.Contains(model, StringComparer.OrdinalIgnoreCase)) continue;
                tried.Add(model);
                try
                {
                    if (needVision)
                    {
                        if (!IsActive(model)) _events.OnStatus($"Switching to {model} to look at the picture...");
                        await UnloadOthersAsync(model, ct).ConfigureAwait(false);   // never two models in RAM at once
                        options = WithKeepAlive(options, "0");                       // big vision model: free RAM right after
                    }
                    else
                    {
                        await UnloadOthersAsync(model, ct).ConfigureAwait(false);   // e.g. gemma left over: free it before loading qwen
                    }
                    var r = await _client.ChatAsync(model, messages, options, onToken, ct).ConfigureAwait(false);
                    if (!needVision) NoticeIfSwitched(model);
                    return r;
                }
                catch (OllamaException ex) when (ex.ModelNotFound)
                {
                    InvalidateCache();
                }
            }
        }
        throw new NoModelAvailableException(needVision, tried.Count > 0 ? tried : Candidates(needVision));
    }
}
