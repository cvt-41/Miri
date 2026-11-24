using System.Text.RegularExpressions;
using Miri.Core;
using static Miri.Brain.TextUtil;

namespace Miri.Brain;

/// <summary>Answers questions about the active window: UI tree text, or a window-only screenshot when the tree is too thin.</summary>
internal sealed class ScreenQuestion
{
    private const int MinElementsForText = 3;
    private static readonly Regex ElementRef = new(@"\[(\d{1,3})\]", RegexOptions.Compiled);

    private readonly LlmService _llm;
    private readonly IScreenReader _reader;
    private readonly IMiriEvents _events;

    public ScreenQuestion(LlmService llm, IScreenReader reader, IMiriEvents events)
    {
        _llm = llm;
        _reader = reader;
        _events = events;
    }

    private static readonly Regex VisualWords = new(
        @"(?<![\p{L}])(?:picture|pictures|image|images|photo|photos|pic|screenshot|look like|looks like|what do you see|" +
        @"can you see|see on|look at|color|colour|colors|logo|icon looks|chart|graph|diagram|drawing|meme|video|" +
        @"face|person|who is this|what is this thing|describe|" +
        @"hình|ảnh|hình ảnh|tấm ảnh|bức ảnh|màu|màu sắc|nhìn|thấy gì|trông|biểu đồ|logo|khuôn mặt|người này|ai đây|mô tả)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True when answering needs eyes, not just the list of controls ("what's in this picture?", "màu gì?").</summary>
    internal static bool NeedsVision(string question) =>
        VisualWords.IsMatch(question.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormC));

    /// <returns>The reply text.</returns>
    public async Task<string> AnswerAsync(string question, Lang lang, IntPtr ownWindow, IReadOnlyList<ChatTurn> turns,
        ReplyWriter reply, CancellationToken ct)
    {
        var snapshot = _reader.SnapshotForeground(ownWindow);
        if (snapshot == null)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, "I can't see any window right now, meow.", "Miri chưa thấy cửa sổ nào cả, meo."));
            return reply.Text;
        }

        var title = string.IsNullOrWhiteSpace(snapshot.WindowTitle) ? snapshot.ProcessName : snapshot.WindowTitle;
        _events.OnStatus(L(lang, $"Looking at {Truncate(title, 40)}...", $"Đang xem {Truncate(title, 40)}..."));
        _events.OnMood(PetMood.Thinking);

        // A screenshot (and the vision model) when the question is about how things LOOK, or the UI tree is too thin.
        bool useScreenshot = snapshot.Elements.Count < MinElementsForText || NeedsVision(question);
        string? image = null;
        if (useScreenshot)
        {
            try { image = ScreenCapture.CaptureWindowPngBase64(snapshot.WindowBounds); }
            catch (Exception) { image = null; }
            if (image == null) useScreenshot = false;   // fall back to whatever text we have
        }

        var messages = new List<OllamaMessage> { OllamaMessage.System(Persona.ScreenSystemPrompt(lang, useScreenshot)) };
        foreach (var t in turns.TakeLast(2))
        {
            messages.Add(OllamaMessage.User(t.User));
            messages.Add(OllamaMessage.Assistant(t.Assistant));
        }

        if (useScreenshot)
        {
            _events.OnStatus(L(lang, "Taking a peek at the window...", "Miri đang nhìn kỹ cửa sổ..."));
            messages.Add(OllamaMessage.User(
                $"Screenshot of my active window \"{snapshot.WindowTitle}\" ({snapshot.ProcessName}).\n\nQuestion: {question}",
                new[] { image! }));
        }
        else
        {
            messages.Add(OllamaMessage.User($"{AgentLoop.SafePromptText(snapshot)}\n\nQuestion: {question}"));
        }

        var result = await _llm.ChatAsync(messages,
            new OllamaChatOptions { Temperature = 0.3, NumPredict = 400 },
            reply.Token, ct, needVision: useScreenshot).ConfigureAwait(false);

        if (!useScreenshot)
        {
            foreach (Match m in ElementRef.Matches(result.Content))
            {
                if (!int.TryParse(m.Groups[1].Value, out var id)) continue;
                var el = snapshot.Elements.FirstOrDefault(e => e.Id == id);
                if (el == null || el.Bounds.IsEmpty) continue;
                _events.OnMood(PetMood.Pointing);
                _events.OnPointAt(el.Bounds);
                return reply.Text;
            }
        }

        _events.OnMood(PetMood.Happy);
        return reply.Text;
    }
}
