using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Miri.Brain;

/// <summary>One chat message for Ollama's /api/chat.</summary>
public sealed class OllamaMessage
{
    public OllamaMessage(string role, string content, IReadOnlyList<string>? imagesBase64 = null)
    {
        Role = role;
        Content = content;
        Images = imagesBase64;
    }

    public string Role { get; }
    public string Content { get; }
    public IReadOnlyList<string>? Images { get; }

    public static OllamaMessage System(string content) => new("system", content);
    public static OllamaMessage User(string content, IReadOnlyList<string>? images = null) => new("user", content, images);
    public static OllamaMessage Assistant(string content) => new("assistant", content);
}

/// <summary>A tool call returned by the model. Arguments is always a JSON object (coerced).</summary>
public sealed record OllamaToolCall(string Name, JsonElement Arguments);

public sealed record OllamaChatResult(string Model, string Content, IReadOnlyList<OllamaToolCall> ToolCalls);

public sealed record OllamaModelInfo(string Name, IReadOnlyList<string> Capabilities);

public sealed class OllamaChatOptions
{
    public double? Temperature { get; init; }
    public int? NumPredict { get; init; }

    /// <summary>
    /// The ONE context size used for every request. Ollama reloads the model whenever num_ctx changes
    /// (tens of seconds on CPU), so all calls - chat, routing, memory, agent, warm-up - must share it.
    /// </summary>
    public const int SharedNumCtx = 4096;

    public int NumCtx { get; init; } = SharedNumCtx;
    /// <summary>Ollama keep_alive for this request ("0" unloads right after). Null = the client's default.</summary>
    public string? KeepAlive { get; init; }
    /// <summary>Ollama "think" flag. Miri chats with thinking off (CPU is slow).</summary>
    public bool Think { get; init; }
    /// <summary>Ollama/OpenAI-style tool definitions (array of {type:"function", function:{...}}).</summary>
    public JsonArray? Tools { get; init; }
}

public sealed class OllamaException : Exception
{
    public OllamaException(string message, HttpStatusCode? status = null) : base(message)
    {
        Status = status;
    }

    public HttpStatusCode? Status { get; }

    public bool ModelNotFound =>
        Status == HttpStatusCode.NotFound ||
        (Message.Contains("model", StringComparison.OrdinalIgnoreCase) &&
         Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Minimal streaming client for a local Ollama server.</summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public OllamaClient(string baseUrl, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromMinutes(15);   // CPU inference can be very slow
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan,            // we enforce _timeout ourselves (covers body streaming too)
        };
    }

    public void Dispose() => _http.Dispose();

    /// <summary>GET /api/tags.</summary>
    public async Task<IReadOnlyList<OllamaModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await _http.GetAsync("api/tags", cts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new OllamaException(ExtractError(body) ?? $"Ollama /api/tags returned {(int)resp.StatusCode}", resp.StatusCode);

        var list = new List<OllamaModelInfo>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in models.EnumerateArray())
            {
                var name = m.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;
                var caps = new List<string>();
                if (m.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
                    caps.AddRange(c.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
                list.Add(new OllamaModelInfo(name!, caps));
            }
        }
        return list;
    }

    /// <summary>POST /api/chat with stream=true. Content tokens are pushed to onToken as they arrive.</summary>
    public async Task<OllamaChatResult> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages,
        OllamaChatOptions? options, Action<string>? onToken, CancellationToken ct = default)
    {
        options ??= new OllamaChatOptions();
        try
        {
            return await ChatCoreAsync(model, messages, options, sendThink: true, onToken, ct).ConfigureAwait(false);
        }
        catch (OllamaException ex) when (ex.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // Model doesn't support the think flag at all: retry without it.
            return await ChatCoreAsync(model, messages, options, sendThink: false, onToken, ct).ConfigureAwait(false);
        }
    }

    private async Task<OllamaChatResult> ChatCoreAsync(string model, IReadOnlyList<OllamaMessage> messages,
        OllamaChatOptions options, bool sendThink, Action<string>? onToken, CancellationToken ct)
    {
        var payload = BuildPayload(model, messages, options, sendThink);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);

        using var req = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        try
        {
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                throw new OllamaException(ExtractError(errBody) ?? $"Ollama returned {(int)resp.StatusCode}", resp.StatusCode);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var content = new StringBuilder();
            var toolCalls = new List<OllamaToolCall>();
            string usedModel = model;

            while (true)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    throw new OllamaException(err.ValueKind == JsonValueKind.String ? err.GetString()! : err.ToString());

                if (root.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String)
                    usedModel = mEl.GetString() ?? model;

                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var piece = c.GetString();
                        if (!string.IsNullOrEmpty(piece))
                        {
                            content.Append(piece);
                            onToken?.Invoke(piece);
                        }
                    }
                    if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            var parsed = ParseToolCall(tc);
                            if (parsed != null) toolCalls.Add(parsed);
                        }
                    }
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                    break;
            }

            return new OllamaChatResult(usedModel, content.ToString(), toolCalls);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Ollama did not finish within {_timeout.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>Default keep_alive for requests that don't set one (from MiriSettings.ModelKeepAlive).</summary>
    public static string DefaultKeepAlive { get; set; } = "15s";

    private static JsonNode ParseKeepAlive(string v) =>
        int.TryParse(v, out var seconds) ? JsonValue.Create(seconds)! : JsonValue.Create(v)!;

    /// <summary>Models currently loaded in RAM (GET /api/ps).</summary>
    public async Task<IReadOnlyList<string>> ListLoadedAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var resp = await _http.GetAsync("api/ps", cts.Token).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        var list = new List<string>();
        if (!resp.IsSuccessStatusCode) return list;
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var m in models.EnumerateArray())
                if (m.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name) list.Add(name);
        return list;
    }

    /// <summary>Frees a model's RAM now (POST /api/generate with keep_alive 0). Best effort.</summary>
    public async Task UnloadAsync(string model, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var payload = new JsonObject { ["model"] = model, ["keep_alive"] = 0 };
            using var content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync("api/generate", content, cts.Token).ConfigureAwait(false);
        }
        catch (Exception) { /* not loaded / Ollama gone: nothing to free */ }
    }

    private static JsonObject BuildPayload(string model, IReadOnlyList<OllamaMessage> messages, OllamaChatOptions options, bool sendThink)
    {
        var msgs = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
            if (m.Images is { Count: > 0 })
            {
                var imgs = new JsonArray();
                foreach (var i in m.Images) imgs.Add(i);
                o["images"] = imgs;
            }
            msgs.Add(o);
        }

        var opts = new JsonObject { ["num_ctx"] = options.NumCtx };
        if (options.Temperature is { } t) opts["temperature"] = t;
        if (options.NumPredict is { } np) opts["num_predict"] = np;

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = msgs,
            ["stream"] = true,
            ["keep_alive"] = ParseKeepAlive(options.KeepAlive ?? DefaultKeepAlive),
            ["options"] = opts,
        };
        if (sendThink) payload["think"] = options.Think;
        if (options.Tools is { Count: > 0 })
            payload["tools"] = JsonNode.Parse(options.Tools.ToJsonString());   // deep clone
        return payload;
    }

    /// <summary>Parses {function:{name, arguments}}; arguments may be an object or a JSON string; id may be number/string (ignored).</summary>
    internal static OllamaToolCall? ParseToolCall(JsonElement tc)
    {
        var fn = tc.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : tc;
        if (!fn.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        var name = nameEl.GetString();
        if (string.IsNullOrWhiteSpace(name)) return null;

        JsonElement args = default;
        bool have = false;
        foreach (var key in new[] { "arguments", "parameters" })
        {
            if (!fn.TryGetProperty(key, out var a)) continue;
            if (a.ValueKind == JsonValueKind.Object) { args = a.Clone(); have = true; break; }
            if (a.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var d = JsonDocument.Parse(a.GetString() ?? "{}");
                    if (d.RootElement.ValueKind == JsonValueKind.Object) { args = d.RootElement.Clone(); have = true; break; }
                }
                catch (JsonException) { }
            }
        }
        if (!have)
        {
            using var empty = JsonDocument.Parse("{}");
            args = empty.RootElement.Clone();
        }
        return new OllamaToolCall(name!.Trim(), args);
    }

    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.ToString();
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}
