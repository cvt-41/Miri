using System.Net.Http;
using System.Text.RegularExpressions;
using Miri.Core;
using static Miri.Brain.TextUtil;

namespace Miri.Brain;

/// <summary>Miri's brain: routes a user message and answers / acts, streaming through <see cref="IMiriEvents"/>.</summary>
public sealed class MiriBrain : IMiriBrain, IDisposable
{
    private static readonly Regex FirstPerson = new(
        @"(?<![\p{L}\p{M}])(i|i'm|im|i've|my|me|mine|tôi|mình|tớ|em|tui|toi|minh)(?![\p{L}\p{M}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const int ChatHistoryTurns = 4;
    private const int ChatHistoryReplyChars = 360;

    private readonly MiriSettings _settings;
    private readonly IMiriEvents _events;
    private readonly OllamaClient _client;
    private readonly LlmService _llm;
    private readonly IntentRouter _router;
    private readonly IKnowledgeSearch _knowledge;
    private readonly MemoryStore _memory;
    private readonly AgentLoop _agent;
    private readonly ScreenQuestion _screenQuestion;
    private readonly Weather _weather;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private string _activeModel;

    public MiriBrain(MiriSettings settings, IScreenReader reader, IActionExecutor executor, IApprovalGate approval,
        IMiriEvents events, string knowledgeDir)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _activeModel = string.IsNullOrWhiteSpace(settings.ChatModel) ? "qwen3.5:4b" : settings.ChatModel;

        _client = new OllamaClient(settings.OllamaUrl, TimeSpan.FromMinutes(15));
        OllamaClient.DefaultKeepAlive = string.IsNullOrWhiteSpace(settings.ModelKeepAlive) ? "15s" : settings.ModelKeepAlive.Trim();
        _llm = new LlmService(_client, settings, events, () => ActiveModel);
        _router = new IntentRouter(_llm);
        _weather = new Weather(_settings);
        _knowledge = new KnowledgeBase(knowledgeDir);
        _memory = new MemoryStore(Path.Combine(MiriSettings.DataDir, "memory.db"));
        _agent = new AgentLoop(_llm, settings, reader, executor, approval, events);
        _screenQuestion = new ScreenQuestion(_llm, reader, events);
    }

    /// <summary>Miri's own window (pet/bubble), excluded from screen snapshots. Set by the App after the window is created.</summary>
    public IntPtr OwnWindow { get; set; }

    public string ActiveModel
    {
        get => Volatile.Read(ref _activeModel);
        set
        {
            if (!string.IsNullOrWhiteSpace(value)) Volatile.Write(ref _activeModel, value.Trim());
        }
    }

    public async Task<IReadOnlyList<string>> AvailableModelsAsync()
    {
        try
        {
            var models = await _llm.GetModelsAsync(CancellationToken.None, refresh: true).ConfigureAwait(false);
            return models.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    public async Task HandleAsync(string userText, CancellationToken ct)
    {
        userText = (userText ?? "").Trim();
        var lang = DetectLanguage(userText);
        if (userText.Length == 0) return;

        var reply = new ReplyWriter(_events);
        CancelBackgroundMemory();   // a pending/running memory extraction must never delay a new question
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _events.OnMood(PetMood.Thinking);
            if (Weather.IsWeatherQuestion(userText))
            {
                _events.OnStatus(L(lang, "Sniffing the air...", "Miri đang ngửi gió..."));
                string w;
                try { w = await _weather.AnswerAsync(userText, lang, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    w = L(lang, "I couldn't reach the weather service, meow. Is the internet on?", "Miri không kết nối được dịch vụ thời tiết, meo. Mạng có bật không?");
                }
                reply.Say(w);
                _events.OnMood(PetMood.Happy);
                _memory.AddTurn(userText, w);
                return;
            }
            var intent = await _router.RouteAsync(userText, ct).ConfigureAwait(false);

            switch (intent)
            {
                case Intent.ScreenTask:
                {
                    var said = await _agent.RunAsync(userText, lang, OwnWindow, reply, ct).ConfigureAwait(false);
                    _memory.AddTurn(userText, said);
                    break;
                }
                case Intent.ScreenQuestion:
                {
                    var said = await _screenQuestion.AnswerAsync(userText, lang, OwnWindow, _memory.RecentTurns(), reply, ct).ConfigureAwait(false);
                    _memory.AddTurn(userText, said);
                    break;
                }
                default:
                    await AnswerAsync(userText, intent, lang, reply, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _events.OnMood(PetMood.Idle);
            _events.OnStatus(L(lang, "Stopped.", "Đã dừng."));
            throw;
        }
        catch (Exception ex)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(FriendlyError(ex, lang));
        }
        finally
        {
            _busy.Release();
            ScheduleBackgroundMemory();
        }
    }

    private async Task AnswerAsync(string userText, Intent intent, Lang lang, ReplyWriter reply, CancellationToken ct)
    {
        // Notes only cover coffee and cats; adding them to unrelated questions just slows the first word down.
        var knowledge = intent == Intent.Question && IntentRouter.MentionsKnowledgeTopic(userText)
            ? _knowledge.Search(userText, 2)
            : Array.Empty<KnowledgeChunk>();
        if (knowledge.Count > 0)
            _events.OnStatus(L(lang, "Checking my notes...", "Miri đang xem sổ tay..."));

        // On CPU, time-to-first-token is dominated by prompt evaluation (~50 tokens/s for a 4B model), so the
        // prompt is kept small: capped notes and facts, the last few turns with Miri's replies shortened.
        // Stable prefix first (persona + facts + history) so models that support prefix caching can reuse it;
        // the per-turn language rule and reference notes go last, right before the question.
        var messages = new List<OllamaMessage>
        {
            OllamaMessage.System(Persona.ChatSystemPrompt(_memory.RecentFacts())),
        };
        foreach (var t in _memory.RecentTurns().TakeLast(ChatHistoryTurns))
        {
            messages.Add(OllamaMessage.User(t.User));
            messages.Add(OllamaMessage.Assistant(Truncate(t.Assistant, ChatHistoryReplyChars)));
        }
        messages.Add(OllamaMessage.System(Persona.ChatTurnContext(lang, knowledge)));
        messages.Add(OllamaMessage.User(userText));

        var result = await _llm.ChatAsync(messages,
            new OllamaChatOptions { Temperature = 0.6, NumPredict = 400 },
            reply.Token, ct).ConfigureAwait(false);

        var answer = result.Content.Trim();
        if (answer.Length == 0)
        {
            answer = lang == Lang.Vietnamese ? Persona.NotSureVi : Persona.NotSureEn;
            reply.Say(answer);
        }
        _events.OnMood(PetMood.Happy);
        _memory.AddTurn(userText, answer);

        if (WorthRemembering(userText))
            EnqueueMemory(userText);   // extracted later, only while idle (see ScheduleBackgroundMemory)
    }

    // ------------------------------------------------------------------ warm-up

    /// <summary>
    /// Loads the chat model (same num_ctx as every other call) and pre-fills the persona prompt prefix,
    /// so the first real question doesn't pay model-load time. Best effort; never throws.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct = default)
    {
        try
        {
            var messages = new[]
            {
                OllamaMessage.System(Persona.ChatSystemPrompt(_memory.RecentFacts())),
                OllamaMessage.User("hi"),
            };
            await _llm.ChatAsync(messages, new OllamaChatOptions { Temperature = 0, NumPredict = 1 }, onToken: null, ct)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ollama not running / no model: the first real question reports it properly.
        }
    }

    // ------------------------------------------------------------------ background memory extraction
    // Ollama on CPU effectively serves one request at a time, so the extraction call must never compete
    // with the user's next question: it waits until Miri has been idle for a moment, runs under _busy,
    // and is cancelled (and kept queued for the next idle period) as soon as a new question arrives.

    private static readonly TimeSpan MemoryIdleDelay = TimeSpan.FromSeconds(4);
    private const int MaxPendingMemory = 3;

    private readonly object _memoryLock = new();
    private readonly Queue<string> _pendingMemory = new();
    private CancellationTokenSource? _memoryCts;

    private static bool WorthRemembering(string text) =>
        text.Length >= 16 &&
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length >= 4 &&
        FirstPerson.IsMatch(text);

    private void EnqueueMemory(string text)
    {
        lock (_memoryLock)
        {
            _pendingMemory.Enqueue(text);
            while (_pendingMemory.Count > MaxPendingMemory) _pendingMemory.Dequeue();
        }
    }

    private void CancelBackgroundMemory()
    {
        lock (_memoryLock)
        {
            _memoryCts?.Cancel();
            _memoryCts = null;
        }
    }

    /// <summary>Called after every handled message: starts the idle timer for any queued extraction.</summary>
    private void ScheduleBackgroundMemory()
    {
        CancellationToken token;
        lock (_memoryLock)
        {
            if (_pendingMemory.Count == 0) return;
            _memoryCts?.Cancel();
            _memoryCts = new CancellationTokenSource();
            token = _memoryCts.Token;
        }
        _ = Task.Run(() => RunMemoryWhenIdleAsync(token));
    }

    private async Task RunMemoryWhenIdleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(MemoryIdleDelay, ct).ConfigureAwait(false);
            if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false)) return;   // a question is running; it reschedules
            try
            {
                while (true)
                {
                    string? text;
                    lock (_memoryLock)
                        if (!_pendingMemory.TryPeek(out text)) return;

                    await ExtractPreferenceAsync(text, ct).ConfigureAwait(false);   // throws if a question arrives

                    lock (_memoryLock)
                        if (_pendingMemory.TryPeek(out var head) && ReferenceEquals(head, text)) _pendingMemory.Dequeue();
                }
            }
            finally
            {
                _busy.Release();
            }
        }
        catch (Exception)
        {
            // Cancelled by a new question (item stays queued) or brain disposed. Memory is best-effort.
        }
    }

    private async Task ExtractPreferenceAsync(string userText, CancellationToken ct)
    {
        const string system =
            "You extract long-term memory for a personal assistant. " +
            "From the user's message, extract ONE durable personal preference or fact about the user that is worth remembering " +
            "for future conversations (for example: \"User likes strong coffee with condensed milk\", \"User works from home\", " +
            "\"User has a white cat named Bông\"). Ignore questions, temporary states and requests. " +
            "Write it as one short sentence in English starting with \"User\". If there is nothing durable, reply exactly NONE.";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            var r = await _llm.ChatAsync(
                new[] { OllamaMessage.System(system), OllamaMessage.User(userText) },
                new OllamaChatOptions { Temperature = 0, NumPredict = 48 },   // shared num_ctx: no model reload
                onToken: null, cts.Token).ConfigureAwait(false);

            var fact = r.Content.Trim().Trim('"', '\'', '`', '-', '*', ' ').Split('\n')[0].Trim();
            if (fact.Length < 8 || fact.Length > 200) return;
            if (fact.Contains("NONE", StringComparison.OrdinalIgnoreCase)) return;
            if (!fact.StartsWith("User", StringComparison.OrdinalIgnoreCase)) return;
            _memory.AddFact(fact);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // a new question arrived: keep the message queued for the next idle period
        }
        catch (Exception)
        {
            // Memory is best-effort.
        }
    }

    private string FriendlyError(Exception ex, Lang lang)
    {
        var url = _settings.OllamaUrl;
        switch (ex)
        {
            case NoModelAvailableException nm when nm.NeedVision:
                return L(lang,
                    "I need a vision model to look at this window, but none is installed. Try `ollama pull qwen3.5:4b`, meow.",
                    "Miri cần mô hình có thị giác để nhìn cửa sổ này mà chưa có. Bạn thử `ollama pull qwen3.5:4b` nhé, meo.");
            case NoModelAvailableException:
                return L(lang,
                    $"None of my models are installed in Ollama ({_settings.ChatModel}, {_settings.AlternateModel}, {_settings.FallbackModel}). Try `ollama pull {_settings.ChatModel}`, meow.",
                    $"Ollama chưa có mô hình nào của Miri ({_settings.ChatModel}, {_settings.AlternateModel}, {_settings.FallbackModel}). Bạn thử `ollama pull {_settings.ChatModel}` nhé, meo.");
            case HttpRequestException:
                return L(lang,
                    $"Meow... I can't reach my brain. Is Ollama running at {url}?",
                    $"Meo... Miri không kết nối được với Ollama. Bạn kiểm tra xem Ollama có đang chạy ở {url} không nhé?");
            case TimeoutException:
            case TaskCanceledException:
                return L(lang,
                    "That took way too long and I gave up, meow. Try again or pick a smaller model.",
                    "Lâu quá nên Miri bỏ cuộc rồi, meo. Bạn thử lại hoặc chọn mô hình nhỏ hơn nhé.");
            case OllamaException oe:
                return L(lang,
                    $"Ollama complained: {Truncate(oe.Message, 120)}. Grr.",
                    $"Ollama báo lỗi: {Truncate(oe.Message, 120)}. Grr.");
            default:
                return L(lang,
                    $"Something went wrong in my head, meow ({Truncate(ex.Message, 100)}).",
                    $"Đầu Miri bị rối rồi, meo ({Truncate(ex.Message, 100)}).");
        }
    }

    public void Dispose()
    {
        CancelBackgroundMemory();
        _client.Dispose();
        _busy.Dispose();
    }
}
