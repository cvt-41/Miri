using System.Text;
using System.Text.RegularExpressions;
using Miri.Core;

namespace Miri.Brain;

/// <summary>Cheap VI+EN keyword heuristics first; falls back to a one-word LLM classification.</summary>
internal sealed class IntentRouter
{
    private readonly LlmService _llm;

    public IntentRouter(LlmService llm) => _llm = llm;

    private static Regex Words(params string[] words)
    {
        var alt = string.Join("|", words.OrderByDescending(w => w.Length).Select(Regex.Escape));
        return new Regex(@"(?<![\p{L}\p{M}\p{Nd}])(?:" + alt + @")(?![\p{L}\p{M}\p{Nd}])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // Unambiguous UI verbs (matched on lower-cased text WITH diacritics, so "mở" ≠ "mờ").
    private static readonly Regex ActionVerbs = Words(
        "click", "double-click", "double click", "right-click", "right click", "press", "tap", "type", "open",
        "launch", "scroll", "close", "fill in", "play", "switch to", "minimize", "maximize", "hit enter",
        "bấm", "nhấn", "nhấp", "gõ", "cuộn", "mở", "đóng", "điền", "chuyển sang", "khởi động");

    // Verbs that are just as common in ordinary questions ("write me a poem", "chọn giống mèo nào"):
    // they only make a screen task when an app or screen thing is mentioned too.
    private static readonly Regex AmbiguousVerbs = Words(
        "send", "write", "search for", "search", "go to", "start", "check", "select", "reply", "find",
        "gửi", "viết", "tìm", "vào", "chọn", "nhập", "bật", "tắt", "trả lời", "soạn");

    private static readonly Regex AppWords = Words(
        "email", "e-mail", "mail", "gmail", "outlook", "message", "messages", "chat", "notepad", "word", "excel",
        "powerpoint", "chrome", "edge", "firefox", "browser", "tab", "window", "app", "file", "folder", "desktop",
        "settings", "sonora", "music app", "wifi", "wi-fi", "bluetooth", "volume", "telegram", "zalo", "messenger", "discord", "facebook",
        "youtube", "google", "spotify", "calculator", "explorer", "taskbar", "button", "menu", "textbox", "text box",
        "tin nhắn", "thư", "trình duyệt", "cửa sổ", "ứng dụng", "tệp", "thư mục", "cài đặt", "âm lượng", "máy tính",
        "nút", "trang web", "màn hình");

    private static readonly Regex PolitePrefix = new(
        @"^(?:(?:hey|hi|ok|okay)\s+)?(?:miri[\s,!:]*)?(?:(?:please|pls|can you|could you|would you|will you|help me(?: to)?|i want you to|go ahead and|" +
        @"miri ơi|ơi|hãy|làm ơn|giúp tôi|giúp mình|giúp em|giúp tớ|giúp|bạn|cậu|em|cho tôi|cho mình|nhờ)[\s,]+)*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ScreenRefs = Words(
        "on my screen", "on the screen", "on screen", "my screen", "this screen", "this button", "this window",
        "this app", "this page", "this dialog", "this popup", "this icon", "this menu", "this tab", "this error",
        "this picture", "this image", "this photo", "this chart", "this video", "what do you see", "can you see",
        "look at my screen", "look at this", "describe my screen", "describe this",
        "hình này", "ảnh này", "tấm ảnh này", "bức ảnh này", "biểu đồ này", "video này", "bạn thấy gì", "nhìn màn hình", "nhìn giúp",
        "what am i looking at", "what's open", "what is open", "current window", "active window",
        "trên màn hình", "màn hình", "nút này", "cửa sổ này", "ứng dụng này", "trang này", "hộp thoại này",
        "biểu tượng này", "menu này", "tab này", "lỗi này", "cái này là gì", "đang mở");

    private static readonly Regex HowTo = Words(
        "how do i", "how to", "how can i", "how should i", "where is", "where do", "where can", "what is", "what does",
        "what's", "which", "why", "explain", "is it", "should i",
        "làm sao", "làm thế nào", "như thế nào", "thế nào", "cách", "ở đâu", "là gì", "tại sao", "vì sao",
        "có nên", "nghĩa là gì", "giải thích", "gì", "nào", "không?");

    private static readonly Regex Greetings = Words(
        "hi", "hello", "hey", "yo", "good morning", "good night", "good evening", "thanks", "thank you", "thx",
        "bye", "goodbye", "how are you", "love you", "cute", "good kitty", "good cat",
        "chào", "xin chào", "chào miri", "cảm ơn", "cám ơn", "tạm biệt", "khỏe không", "dễ thương", "ngoan", "yêu");

    private static readonly Regex KnowledgeTopics = Words(
        "coffee", "espresso", "latte", "cappuccino", "brew", "brewing", "beans", "roast", "phin", "moka", "french press",
        "pour-over", "pour over", "cold brew", "grind", "arabica", "robusta",
        "cat", "cats", "kitten", "kittens", "breed", "adopt", "adoption", "vet", "vaccine", "neuter", "spay", "deaf",
        "cà phê", "cafe", "cà-phê", "pha", "hạt", "rang", "xay", "sữa đá", "mèo", "mèo con", "giống mèo", "nhận nuôi",
        "tiêm phòng", "triệt sản", "điếc", "thú y");

    public async Task<Intent> RouteAsync(string userText, CancellationToken ct)
    {
        var heuristic = Heuristic(userText);
        if (heuristic != null) return heuristic.Value;
        return await ClassifyWithLlmAsync(userText, ct).ConfigureAwait(false);
    }

    /// <summary>True when the text mentions a topic the knowledge notes cover (coffee, cats).</summary>
    internal static bool MentionsKnowledgeTopic(string text) =>
        KnowledgeTopics.IsMatch(text.ToLowerInvariant().Normalize(NormalizationForm.FormC));

    /// <summary>Removes "please", "can you", "Miri ơi", "giúp mình"... from the start of a request.</summary>
    internal static string StripPolite(string lowered) => PolitePrefix.Replace(lowered, "").TrimStart();

    internal static Intent? Heuristic(string userText)
    {
        var t = userText.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC);
        if (t.Length == 0) return Intent.Chitchat;

        var stripped = StripPolite(t);
        bool isHowTo = HowTo.IsMatch(t) || t.EndsWith('?');
        bool hasScreenRef = ScreenRefs.IsMatch(t);
        bool hasApp = AppWords.IsMatch(t);
        bool hasKnowledge = KnowledgeTopics.IsMatch(t);
        bool startsWithVerb = ActionVerbs.Match(stripped) is { Success: true, Index: 0 };
        bool startsWithAmbiguous = AmbiguousVerbs.Match(stripped) is { Success: true, Index: 0 };
        int words = t.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        // "play Lạc Trôi on Sonora", "next song", "tạm dừng nhạc"
        if (MediaCommands.TryParse(t, "") != null) return Intent.ScreenTask;

        // "open notepad", "bấm nút Gửi", "mở zalo giúp mình"
        if (startsWithVerb)
            return t.EndsWith('?') && !hasApp && !hasScreenRef ? null : Intent.ScreenTask;   // "can you type faster than me?"

        // "write a poem" (question) vs "write an email in Outlook" (task)
        if (startsWithAmbiguous)
        {
            if (hasApp || hasScreenRef) return Intent.ScreenTask;
            return Intent.Question;   // "write me a poem", "tìm công thức nấu phở", "check if 97 is prime"
        }

        if (hasKnowledge && !hasScreenRef && !hasApp) return Intent.Question;
        if (ActionVerbs.IsMatch(t) && !isHowTo && (hasApp || hasScreenRef || words <= 6)) return Intent.ScreenTask;
        if (hasScreenRef) return Intent.ScreenQuestion;

        if (words <= 5 && Greetings.IsMatch(t) && !hasKnowledge) return Intent.Chitchat;
        if (hasKnowledge || isHowTo) return Intent.Question;   // advice / info question

        // A task verb hiding mid-sentence next to an app ("I need you to send Bob a message on Zalo"):
        // only then spend an LLM round trip. Question and Chitchat are answered the same way.
        if (hasApp && (AmbiguousVerbs.IsMatch(t) || ActionVerbs.IsMatch(t))) return null;
        return words <= 5 ? Intent.Chitchat : Intent.Question;
    }

    private async Task<Intent> ClassifyWithLlmAsync(string userText, CancellationToken ct)
    {
        const string system =
            "You route messages for a desktop assistant cat. Categories:\n" +
            "QUESTION - a general question or a request for advice or information\n" +
            "SCREEN_TASK - the user wants the assistant to operate apps on their computer (click, type, open, send...)\n" +
            "SCREEN_QUESTION - the user asks about what is currently shown on their screen, a window or a button\n" +
            "CHITCHAT - greetings, small talk, thanks, jokes\n" +
            "Examples: \"write me a poem about rain\" = QUESTION; \"find a good name for my cat\" = QUESTION; " +
            "\"check if 17 is prime\" = QUESTION; \"write hello in notepad\" = SCREEN_TASK; " +
            "\"send this to Lan on Zalo\" = SCREEN_TASK; \"what does this button do\" = SCREEN_QUESTION.\n" +
            "Reply with exactly one word: QUESTION, SCREEN_TASK, SCREEN_QUESTION or CHITCHAT.";
        try
        {
            var r = await _llm.ChatAsync(
                new[] { OllamaMessage.System(system), OllamaMessage.User(userText) },
                new OllamaChatOptions { Temperature = 0, NumPredict = 4 },   // same num_ctx as chat: no model reload
                onToken: null, ct).ConfigureAwait(false);
            var word = r.Content.Trim().ToUpperInvariant();
            if (word.Contains("SCREEN_QUESTION") || word.Contains("SCREEN QUESTION")) return Intent.ScreenQuestion;
            if (word.Contains("SCREEN_TASK") || word.Contains("SCREEN TASK") || word.Contains("TASK")) return Intent.ScreenTask;
            if (word.Contains("CHITCHAT") || word.Contains("CHAT")) return Intent.Chitchat;
            return Intent.Question;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // If classification fails, the answer call will surface the real error.
            return Intent.Question;
        }
    }
}
