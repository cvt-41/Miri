using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Miri.Core;
using static Miri.Brain.TextUtil;

namespace Miri.Brain;

/// <summary>
/// ScreenTask loop: snapshot → model picks ONE tool → safety assess → point + approval → execute → re-snapshot.
/// </summary>
internal sealed class AgentLoop
{
    private readonly LlmService _llm;
    private readonly MiriSettings _settings;
    private readonly IScreenReader _reader;
    private readonly IActionExecutor _executor;
    private readonly IApprovalGate _approval;
    private readonly IMiriEvents _events;

    public AgentLoop(LlmService llm, MiriSettings settings, IScreenReader reader, IActionExecutor executor,
        IApprovalGate approval, IMiriEvents events)
    {
        _llm = llm;
        _settings = settings;
        _reader = reader;
        _executor = executor;
        _approval = approval;
        _events = events;
    }

    internal static readonly JsonArray Tools = BuildTools();

    private static JsonArray BuildTools()
    {
        static JsonObject Tool(string name, string description, JsonObject properties, params string[] required)
        {
            var req = new JsonArray();
            foreach (var r in required) req.Add(r);
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = properties,
                        ["required"] = req,
                    },
                },
            };
        }

        static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };

        return new JsonArray
        {
            Tool("click", "Click a UI element of the current window.",
                new JsonObject { ["id"] = P("integer", "Element id from the current list") }, "id"),
            Tool("type_text", "Type text into a text input element (Edit, Document or ComboBox; focuses it first).",
                new JsonObject { ["id"] = P("integer", "Element id of the text box"), ["text"] = P("string", "Text to type") }, "id", "text"),
            Tool("press_keys", "Press a key or shortcut, e.g. \"enter\", \"ctrl+s\", \"alt+tab\".",
                new JsonObject { ["keys"] = P("string", "Keys joined with +") }, "keys"),
            Tool("scroll", "Scroll inside an element. Positive amount scrolls down, negative scrolls up.",
                new JsonObject { ["id"] = P("integer", "Element id to scroll"), ["amount"] = P("integer", "Number of wheel steps, e.g. 3 or -3") }, "id", "amount"),
            Tool("open_app", "Open an application by its plain name (no paths or URLs), e.g. \"notepad\", \"calculator\", \"settings\", \"telegram\".",
                new JsonObject { ["name"] = P("string", "Application name") }, "name"),
            Tool("focus_window", "Switch to an already open window by its title or app name, e.g. \"Sonora\" or \"Notepad\".",
                new JsonObject { ["name"] = P("string", "Window title or app name") }, "name"),
            Tool("done", "Finish the task (completed or impossible) with a short summary for the user.",
                new JsonObject { ["summary"] = P("string", "One or two sentences for the user") }, "summary"),
        };
    }

    /// <param name="ownWindow">Miri's own window handle, excluded from snapshots.</param>
    /// <returns>Text spoken to the user (for chat history).</returns>
    public async Task<string> RunAsync(string task, Lang lang, IntPtr ownWindow, ReplyWriter reply, CancellationToken ct)
    {
        // Music: media keys and "play <song> on Sonora" follow a fixed recipe (small models fumble them).
        if (MediaCommands.TryParse(task, _settings.MusicApp) is { } media)
            return await RunMediaAsync(media, lang, ownWindow, reply, ct).ConfigureAwait(false);

        // "open zalo" / "mở máy tính giúp mình": no need to ask the model which tool to use.
        if (TryParseSimpleOpen(task) is { } appName)
            return await OpenDirectAsync(appName, lang, ownWindow, reply, ct).ConfigureAwait(false);

        return await RunLoopAsync(task, lang, ownWindow, reply, ct).ConfigureAwait(false);
    }

    /// <summary>The general step-by-step loop: snapshot, model picks one tool, approval, execute.</summary>
    private async Task<string> RunLoopAsync(string task, Lang lang, IntPtr ownWindow, ReplyWriter reply, CancellationToken ct)
    {
        string? expectedProcess = null;   // process that should be in front for the next step
        var history = new List<string>();
        bool yesToAll = false;
        int noToolStreak = 0;
        int maxSteps = Math.Max(1, _settings.MaxAgentSteps);


        for (int step = 1; step <= maxSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            // No usable window (desktop only, or everything minimized): the model can still open an app.
            var snapshot = _reader.SnapshotForeground(ownWindow) ?? EmptyDesktop;

            // The user switched windows (or something popped up) without Miri asking for it: stop, don't follow.
            if (expectedProcess != null && snapshot.ProcessName != expectedProcess)
            {
                _events.OnMood(PetMood.Confused);
                reply.Say(L(lang,
                    $"The window changed to {Short(snapshot.WindowTitle, snapshot.ProcessName)}, so I stopped to be safe. Ask me again when you're ready, meow.",
                    $"Cửa sổ đã đổi sang {Short(snapshot.WindowTitle, snapshot.ProcessName)} nên Miri dừng lại cho an toàn. Khi nào sẵn sàng bạn nhờ lại nhé, meo."));
                return reply.Text;
            }

            _events.OnStatus(L(lang, $"Looking at {Short(snapshot.WindowTitle, snapshot.ProcessName)}...",
                                     $"Đang xem {Short(snapshot.WindowTitle, snapshot.ProcessName)}..."));
            _events.OnMood(PetMood.Thinking);

            var messages = new List<OllamaMessage>
            {
                OllamaMessage.System(Persona.AgentSystemPrompt(lang)),
                OllamaMessage.User(BuildStepPrompt(task, history, snapshot, noToolStreak > 0)),
            };

            var result = await _llm.ChatAsync(messages,
                new OllamaChatOptions { Temperature = 0.1, NumPredict = 256, Tools = Tools },
                onToken: null, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var call = result.ToolCalls.FirstOrDefault() ?? TryParseToolCallFromText(result.Content);
            if (call == null)
            {
                noToolStreak++;
                if (noToolStreak >= 2)
                {
                    var text = result.Content.Trim();
                    reply.Say(text.Length > 0 ? text : L(lang, "I'm not sure how to do that here, meow.", "Miri không biết làm việc này ở đây thế nào, meo."));
                    _events.OnMood(PetMood.Confused);
                    return reply.Text;
                }
                step--;   // give the model one free nudge
                continue;
            }
            noToolStreak = 0;

            var name = call.Name.ToLowerInvariant();
            if (name == "done")
            {
                var summary = ArgString(call.Arguments, "summary", "text", "message")
                              ?? L(lang, "Done.", "Xong rồi.");
                reply.Say(summary);
                _events.OnMood(PetMood.Happy);
                return reply.Text;
            }

            var action = ToAction(name, call.Arguments, out var parseError);
            if (action == null)
            {
                history.Add($"{step}. {call.Name}(...) -> error: {parseError}");
                continue;
            }

            UiElementInfo? element = null;
            if (action.ElementId is { } id)
            {
                element = snapshot.Elements.FirstOrDefault(e => e.Id == id);
                if (element == null)
                {
                    history.Add($"{step}. {Describe(action, null)} -> error: there is no element [{id}] on the current screen");
                    continue;
                }
            }

            var risk = _executor.Assess(action, snapshot);
            if (risk.Level == RiskLevel.Blocked)
            {
                _events.OnMood(PetMood.Confused);
                reply.Say(L(lang,
                    $"I won't do that: {risk.Reason}. You'll have to do this part yourself, meow.",
                    $"Miri sẽ không làm việc này: {risk.Reason}. Bạn tự làm phần này nhé, meo."));
                return reply.Text;
            }

            Rectangle? target = element?.Bounds;
            if (target is { IsEmpty: false } rect)
            {
                _events.OnMood(PetMood.Pointing);
                _events.OnPointAt(rect);
            }

            var description = DescribeForUser(action, element, snapshot, lang);
            bool approved;
            if (yesToAll && risk.Level == RiskLevel.Normal)
            {
                approved = true;
            }
            else
            {
                var decision = await _approval.RequestAsync(new ApprovalRequest(action, description, risk, target), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                approved = decision != ApprovalDecision.No;
                // "Yes to all" only ever covers Normal actions; Dangerous ones always ask again.
                if (decision == ApprovalDecision.YesToAllForTask && risk.Level == RiskLevel.Normal) yesToAll = true;
            }

            if (!approved)
            {
                _events.OnMood(PetMood.Idle);
                reply.Say(L(lang, "Okay, I stopped. Nothing else touched.", "Được rồi, Miri dừng lại. Không đụng gì thêm."));
                return reply.Text;
            }

            _events.OnStatus(description);
            _events.OnMood(PetMood.Thinking);
            ActionResult exec;
            try
            {
                exec = await _executor.ExecuteAsync(action, snapshot, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                exec = new ActionResult(false, ex.Message);
            }

            history.Add($"{step}. {Describe(action, element)} -> {(exec.Success ? "ok" : "FAILED")}: {Truncate(exec.Message, 160)}");
            if (!exec.Success)
                _events.OnStatus(L(lang, $"That didn't work: {Truncate(exec.Message, 80)}", $"Không được: {Truncate(exec.Message, 80)}"));

            // Let the UI settle. After opening/switching apps, wait until that app is really in front,
            // so the next step looks at it instead of whatever was there before.
            if (exec.Success && action.Kind is ActionKind.OpenApp or ActionKind.FocusWindow && !string.IsNullOrWhiteSpace(action.Target))
            {
                var (front, _, _) = await BringAppForwardAsync(action.Target!, ownWindow, ct).ConfigureAwait(false);
                expectedProcess = front?.ProcessName;   // unknown → don't enforce on the next step
            }
            else
            {
                await Task.Delay(700, ct).ConfigureAwait(false);
                // keyboard shortcuts like alt+tab / win+... or a click may legitimately open another window
                bool mayChangeWindow = action.Kind == ActionKind.PressKeys || action.Kind == ActionKind.Click;
                expectedProcess = mayChangeWindow ? null : snapshot.ProcessName;
            }
        }

        _events.OnMood(PetMood.Confused);
        reply.Say(L(lang,
            $"I used all {maxSteps} steps and I'm not sure the task is finished. Please check, meow.",
            $"Miri đã dùng hết {maxSteps} bước mà chưa chắc xong việc. Bạn kiểm tra lại giúp nhé, meo."));
        return reply.Text;
    }

    private static readonly UiSnapshot EmptyDesktop =
        new("(no app window is open, only the desktop)", "desktop", IntPtr.Zero, Rectangle.Empty, Array.Empty<UiElementInfo>());

    private static readonly System.Text.RegularExpressions.Regex SimpleOpen = new(
        @"^(?:open|launch|start|run|mở|khởi động|chạy)\s+(?:up\s+)?(?:the\s+|my\s+)?(?:app\s+|application\s+|ứng dụng\s+|phần mềm\s+|app\s+)?" +
        @"(?<name>[\p{L}\p{M}\p{Nd}][\p{L}\p{M}\p{Nd} .+#'&-]{0,38}?)" +
        @"(?:\s+(?:app|application|for me|please|now|lên|giúp(?: tôi| mình| em| tớ)?|cho (?:tôi|mình|em|tớ)|nhé|nha|đi|với|ạ))*\s*[.!?]*$",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex MultiStep = new(
        @"(?<![\p{L}])(?:and|then|và|rồi|xong|sau đó|to|để)(?![\p{L}])|[,;]",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>App name for a plain "open X" request, or null when the task has more steps or isn't an open request.</summary>
    internal static string? TryParseSimpleOpen(string task)
    {
        var t = IntentRouter.StripPolite(task.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormC));
        var m = SimpleOpen.Match(t);
        if (!m.Success) return null;
        var name = m.Groups["name"].Value.Trim();
        if (name.Length == 0 || MultiStep.IsMatch(name)) return null;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(?:a|an|một|cái)\s")) return null;   // "start a timer"
        // "open the file / tab / window / settings page" are on-screen things, not apps
        if (name is "file" or "tab" or "new tab" or "window" or "menu" or "link" or "folder" or "tệp" or "tab mới") return null;
        return name;
    }

    private async Task<string> OpenDirectAsync(string appName, Lang lang, IntPtr ownWindow, ReplyWriter reply, CancellationToken ct)
    {
        var snapshot = _reader.SnapshotForeground(ownWindow) ?? EmptyDesktop;
        var action = new UiAction(ActionKind.OpenApp, Target: appName);

        var risk = _executor.Assess(action, snapshot);
        if (risk.Level == RiskLevel.Blocked)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, $"I won't do that: {risk.Reason}", $"Miri sẽ không làm việc này: {risk.Reason}"));
            return reply.Text;
        }

        var description = DescribeForUser(action, null, snapshot, lang);
        var decision = await _approval.RequestAsync(new ApprovalRequest(action, description, risk, null), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (decision == ApprovalDecision.No)
        {
            _events.OnMood(PetMood.Idle);
            reply.Say(L(lang, "Okay, I won't open it.", "Được rồi, Miri không mở nữa."));
            return reply.Text;
        }

        _events.OnStatus(description);
        ActionResult exec;
        try { exec = await _executor.ExecuteAsync(action, snapshot, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { exec = new ActionResult(false, ex.Message); }

        if (exec.Success)
        {
            _events.OnMood(PetMood.Happy);
            reply.Say(L(lang, $"{exec.Message} Meow.", $"Miri mở rồi đó ({exec.Message.Replace("Opened ", "").TrimEnd('.')}), meo."));
        }
        else
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, exec.Message, $"Miri không mở được: {exec.Message}"));
        }
        return reply.Text;
    }

    // ------------------------------------------------------------------ music recipes

    private async Task<string> RunMediaAsync(MediaCommand cmd, Lang lang, IntPtr ownWindow, ReplyWriter reply, CancellationToken ct)
    {
        var app = cmd.App ?? _settings.MusicApp;
        var appTitle = app.Length > 0 ? char.ToUpperInvariant(app[0]) + app[1..] : app;
        string descEn, descVi;
        switch (cmd.Kind)
        {
            case MediaCommandKind.Control:
                (descEn, descVi) = cmd.Label;
                break;
            case MediaCommandKind.PlayAnything:
                descEn = $"Open {appTitle} and press play";
                descVi = $"Mở {appTitle} và bấm phát nhạc";
                break;
            default:
                descEn = $"Play \"{cmd.Query}\" in {appTitle} (open it, search, start the best match)";
                descVi = $"Phát \"{cmd.Query}\" trong {appTitle} (mở app, tìm kiếm, bật kết quả phù hợp nhất)";
                break;
        }
        var description = L(lang, descEn, descVi);

        // One approval for the whole recipe; every step below is still checked by the safety policy and logged.
        var first = cmd.Kind == MediaCommandKind.Control
            ? new UiAction(ActionKind.PressKeys, Keys: cmd.Keys)
            : new UiAction(ActionKind.OpenApp, Target: app);
        var start = _reader.SnapshotForeground(ownWindow) ?? EmptyDesktop;
        var risk = _executor.Assess(first, start);
        if (risk.Level == RiskLevel.Blocked)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, $"I won't do that: {risk.Reason}", $"Miri sẽ không làm việc này: {risk.Reason}"));
            return reply.Text;
        }
        var decision = await _approval.RequestAsync(new ApprovalRequest(first, description, risk, null), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (decision == ApprovalDecision.No)
        {
            _events.OnMood(PetMood.Idle);
            reply.Say(L(lang, "Okay, no music then.", "Được rồi, không bật nhạc nữa."));
            return reply.Text;
        }
        _events.OnStatus(description);
        _events.OnMood(PetMood.Thinking);

        if (cmd.Kind == MediaCommandKind.Control)
        {
            var r = await Exec(first, start, ct).ConfigureAwait(false);
            _events.OnMood(r.Success ? PetMood.Happy : PetMood.Confused);
            reply.Say(r.Success ? L(lang, $"{descEn}. Done, meow.", $"{descVi} rồi đó, meo.") : r.Message);
            return reply.Text;
        }

        // 1) bring the music app to the front (open it when it is not running)
        var (snap, wasRunning, err) = await BringAppForwardAsync(app, ownWindow, ct).ConfigureAwait(false);
        if (snap == null)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(err ?? L(lang, $"I couldn't get {appTitle} on screen, meow.", $"Miri không mở được {appTitle}, meo."));
            return reply.Text;
        }

        if (cmd.Kind == MediaCommandKind.PlayAnything)
        {
            if (!wasRunning) await Task.Delay(2500, ct).ConfigureAwait(false);   // let it restore its queue
            var r = await Exec(new UiAction(ActionKind.PressKeys, Keys: "playpause"), snap, ct).ConfigureAwait(false);
            _events.OnMood(r.Success ? PetMood.Happy : PetMood.Confused);
            reply.Say(r.Success
                ? L(lang, $"{appTitle} is up and I pressed play. If nothing is queued, tell me a song name, meow.",
                          $"Miri đã mở {appTitle} và bấm phát. Nếu chưa có bài nào trong hàng chờ, bạn nói tên bài nhé, meo.")
                : r.Message);
            return reply.Text;
        }

        // 2) open the app's search page: its own "Search" nav button (leftmost one), else Ctrl+F
        var query = cmd.Query!;
        var appKey = Fold(app).Replace(" ", "");
        bool InApp(UiSnapshot s) =>
            Fold(s.ProcessName).Replace(" ", "").Contains(appKey) || Fold(s.WindowTitle).Replace(" ", "").Contains(appKey);

        var page = _reader.SnapshotForegroundAll(ownWindow) ?? snap;
        var nav = page.Elements
            .Where(e => e.ControlType == "Button" && Fold(e.Name) is "search" or "tim kiem" && !e.Bounds.IsEmpty)
            .OrderBy(e => e.Bounds.Left).FirstOrDefault();
        var open = nav != null
            ? await Exec(new UiAction(ActionKind.Click, ElementId: nav.Id), page, ct).ConfigureAwait(false)
            : await Exec(new UiAction(ActionKind.PressKeys, Keys: "ctrl+f"), page, ct).ConfigureAwait(false);
        if (!open.Success)
            return Stuck(open.Message);

        // 3) type the song into the search box (keyboard, so the app sees real typing), then Enter
        UiSnapshot? searchPage = null;
        UiElementInfo? box = null;
        for (int i = 0; i < 6 && box == null; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            searchPage = _reader.SnapshotForegroundAll(ownWindow);
            if (searchPage == null) continue;
            if (!InApp(searchPage)) return LostFocus();
            box = searchPage.Elements.FirstOrDefault(e => e.ControlType is "Edit" or "ComboBox" && !e.IsPassword);
        }
        if (box == null || searchPage == null)
            return Stuck(L(lang, "I couldn't find its search box.", "Miri không tìm thấy ô tìm kiếm."));

        foreach (var step in new[]
                 {
                     new UiAction(ActionKind.Click, ElementId: box.Id),
                     new UiAction(ActionKind.PressKeys, Keys: "ctrl+a"),
                     new UiAction(ActionKind.TypeText, Text: query),
                     new UiAction(ActionKind.PressKeys, Keys: "enter"),
                 })
        {
            var r = await Exec(step, searchPage, ct).ConfigureAwait(false);
            if (!r.Success) return Stuck(r.Message);
            await Task.Delay(step.Kind == ActionKind.Click ? 300 : 250, ct).ConfigureAwait(false);
        }

        // 4) wait for results, then press the Play button inside the best-matching row (only inside the app)
        UiElementInfo? best = null;
        UiSnapshot? results = null;
        for (int attempt = 0; attempt < 6 && best == null; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1800 : 1200, ct).ConfigureAwait(false);
            results = _reader.SnapshotForegroundAll(ownWindow);
            if (results == null) continue;
            if (!InApp(results)) return LostFocus();
            best = BestSongMatch(results, query);
        }

        if (best != null && results != null)
        {
            var play = PlayButtonInside(results, best);
            _events.OnMood(PetMood.Pointing);
            if (!best.Bounds.IsEmpty) _events.OnPointAt(best.Bounds);
            var r = await Exec(new UiAction(ActionKind.Click, ElementId: (play ?? best).Id), results, ct).ConfigureAwait(false);
            _events.OnMood(r.Success ? PetMood.Happy : PetMood.Confused);
            reply.Say(r.Success
                ? L(lang, $"Playing \"{best.Name}\" in {appTitle}. Enjoy, meow.", $"Đang phát \"{best.Name}\" trong {appTitle}. Nghe vui nhé, meo.")
                : L(lang, $"I found \"{best.Name}\" but couldn't press play: {r.Message}", $"Miri thấy \"{best.Name}\" nhưng không bấm phát được: {r.Message}"));
            return reply.Text;
        }

        // 5) no clear match: stop and let the user pick (never wander into other windows)
        _events.OnMood(PetMood.Confused);
        reply.Say(L(lang,
            $"I searched for \"{query}\" in {appTitle}, but I couldn't spot that song in the results. Pick it from the list, or tell me the exact title, meow.",
            $"Miri đã tìm \"{query}\" trong {appTitle} nhưng không thấy bài đó trong kết quả. Bạn chọn trong danh sách, hoặc nói đúng tên bài nhé, meo."));
        return reply.Text;

        string Stuck(string why)
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, $"I got stuck while searching in {appTitle}: {why}", $"Miri bị kẹt khi tìm trong {appTitle}: {why}"));
            return reply.Text;
        }

        string LostFocus()
        {
            _events.OnMood(PetMood.Confused);
            reply.Say(L(lang, $"{appTitle} lost focus while I was searching, so I stopped. Nothing else was touched.",
                              $"{appTitle} bị mất tiêu điểm khi Miri đang tìm nên Miri dừng lại. Không đụng gì khác."));
            return reply.Text;
        }
    }

    /// <summary>A "Play" button drawn inside a result row (Sonora puts one on every song / artist / album row).</summary>
    internal static UiElementInfo? PlayButtonInside(UiSnapshot snapshot, UiElementInfo row)
    {
        if (row.Bounds.IsEmpty) return null;
        return snapshot.Elements.FirstOrDefault(e =>
            e.Id != row.Id && e.ControlType == "Button" && Fold(e.Name) is "play" or "phat" &&
            !e.Bounds.IsEmpty && row.Bounds.Contains(e.Bounds));
    }

    private async Task<ActionResult> Exec(UiAction action, UiSnapshot snapshot, CancellationToken ct)
    {
        try { return await _executor.ExecuteAsync(action, snapshot, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return new ActionResult(false, ex.Message); }
    }

    /// <summary>Lower-case, no diacritics, punctuation to spaces, single spaces.</summary>
    internal static string Fold(string s)
    {
        var d = (s ?? "").ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Focuses the app's window, opening the app first if needed; waits until it is the foreground window.</summary>
    private async Task<(UiSnapshot? Snapshot, bool WasRunning, string? Error)> BringAppForwardAsync(string app, IntPtr ownWindow, CancellationToken ct)
    {
        var key = Fold(app).Replace(" ", "");
        bool Matches(string process, string title) =>
            Fold(process).Replace(" ", "").Contains(key) || Fold(title).Replace(" ", "").Contains(key);

        var current = _reader.SnapshotForeground(ownWindow);
        if (current != null && Matches(current.ProcessName, current.WindowTitle)) return (current, true, null);

        bool running = _reader.ListWindows().Any(w => Matches(w.Process, w.Title));
        var anchor = current ?? EmptyDesktop;
        var r = running
            ? await Exec(new UiAction(ActionKind.FocusWindow, Target: app), anchor, ct).ConfigureAwait(false)
            : await Exec(new UiAction(ActionKind.OpenApp, Target: app), anchor, ct).ConfigureAwait(false);
        if (!r.Success && !running) return (null, false, r.Message);

        for (int i = 0; i < 30; i++)   // up to ~12 s for a cold start
        {
            await Task.Delay(400, ct).ConfigureAwait(false);
            var s = _reader.SnapshotForeground(ownWindow);
            if (s != null && Matches(s.ProcessName, s.WindowTitle) && s.Elements.Count > 0) return (s, running, null);
            if (i == 10 || i == 20)   // opened but behind another window: try focusing it
                await Exec(new UiAction(ActionKind.FocusWindow, Target: app), s ?? anchor, ct).ConfigureAwait(false);
        }
        return (null, running, null);
    }

    private static readonly HashSet<string> NotSongs = new(StringComparer.Ordinal)
    {
        "search", "home", "back", "forward", "refresh", "settings", "history", "local music", "previous", "next",
        "minimize", "restore", "maximize", "close window", "toggle sidebar", "sort", "pinned", "play", "pause", "shuffle",
        "clear", "browse all", "resume", "previous track", "next track", "repeat", "repeat one", "queue", "lyrics", "mute",
        "now playing", "sessions", "add to favorites", "best match", "songs", "artists",
    };

    /// <summary>The clickable element whose name best matches the song query (title / artist).</summary>
    internal static UiElementInfo? BestSongMatch(UiSnapshot snapshot, string query)
    {
        var q = Fold(query);
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (qWords.Length == 0) return null;
        UiElementInfo? best = null;
        int bestScore = 0;
        foreach (var e in snapshot.Elements)
        {
            if (e.ControlType is not ("Button" or "ListItem" or "DataItem" or "Hyperlink" or "TreeItem" or "MenuItem" or "Custom" or "Text"))
                continue;
            if (!e.IsEnabled || string.IsNullOrWhiteSpace(e.Name)) continue;
            var n = Fold(e.Name);
            if (NotSongs.Contains(n)) continue;
            int score;
            if (n == q) score = 100;
            else if (n.Contains(q, StringComparison.Ordinal)) score = 80;
            else
            {
                var nWords = n.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                int hit = qWords.Count(w => nWords.Contains(w));
                if (hit == 0 || hit * 2 < qWords.Length) continue;
                score = 40 + 40 * hit / qWords.Length;
            }
            if (e.ControlType == "Text") score -= 15;   // plain labels are less likely to be the clickable row
            if (score > bestScore) { best = e; bestScore = score; }
        }
        return bestScore >= 60 ? best : null;
    }

    private static string Short(string title, string process)
    {
        var t = string.IsNullOrWhiteSpace(title) ? process : title;
        return Truncate(t, 40);
    }

    private static string BuildStepPrompt(string task, List<string> history, UiSnapshot snapshot, bool nudge)
    {
        var sb = new StringBuilder();
        sb.Append("Task: ").Append(task).Append("\n\n");
        sb.Append("Steps so far:\n");
        if (history.Count == 0) sb.Append("(none yet)\n");
        foreach (var h in history) sb.Append(h).Append('\n');
        sb.Append("\nCurrent screen:\n").Append(SafePromptText(snapshot)).Append("\n\n");
        sb.Append(nudge
            ? "You MUST respond by calling exactly one of the tools now (use done if the task is finished or impossible)."
            : "Call exactly one tool now.");
        return sb.ToString();
    }

    /// <summary>ToPromptText with password values removed (defence in depth; Hands should not expose them anyway).</summary>
    internal static string SafePromptText(UiSnapshot snapshot)
    {
        if (!snapshot.Elements.Any(e => e.IsPassword && !string.IsNullOrEmpty(e.Value)))
            return snapshot.ToPromptText();
        var masked = snapshot.Elements.Select(e => e.IsPassword ? e with { Value = null } : e).ToList();
        return (snapshot with { Elements = masked }).ToPromptText();
    }

    private static UiAction? ToAction(string name, JsonElement args, out string error)
    {
        error = "";
        switch (name)
        {
            case "click":
            {
                var id = ArgInt(args, "id", "element_id", "elementId");
                if (id == null) { error = "missing id"; return null; }
                return new UiAction(ActionKind.Click, ElementId: id);
            }
            case "type_text":
            {
                var id = ArgInt(args, "id", "element_id", "elementId");
                var text = ArgString(args, "text", "value");
                if (id == null || text == null) { error = "type_text needs id and text"; return null; }
                return new UiAction(ActionKind.TypeText, ElementId: id, Text: text);
            }
            case "press_keys":
            {
                var keys = ArgString(args, "keys", "key", "shortcut");
                if (string.IsNullOrWhiteSpace(keys)) { error = "missing keys"; return null; }
                return new UiAction(ActionKind.PressKeys, Keys: keys.Trim());
            }
            case "scroll":
            {
                var id = ArgInt(args, "id", "element_id", "elementId");
                var amount = ArgInt(args, "amount", "delta", "steps") ?? 3;
                if (id == null) { error = "missing id"; return null; }
                return new UiAction(ActionKind.Scroll, ElementId: id, ScrollAmount: amount);
            }
            case "focus_window":
            {
                var w = ArgString(args, "name", "title", "window", "app");
                if (string.IsNullOrWhiteSpace(w)) { error = "focus_window needs a name"; return null; }
                return new UiAction(ActionKind.FocusWindow, Target: w.Trim());
            }
            case "open_app":
            {
                var app = ArgString(args, "name", "app", "application");
                if (string.IsNullOrWhiteSpace(app)) { error = "missing name"; return null; }
                return new UiAction(ActionKind.OpenApp, Target: app.Trim());
            }
            default:
                error = $"unknown tool '{name}'";
                return null;
        }
    }

    private static string Describe(UiAction a, UiElementInfo? e)
    {
        string El() => e == null ? $"[{a.ElementId}]" : $"[{e.Id}] {e.ControlType} \"{Truncate(e.Name, 40)}\"";
        return a.Kind switch
        {
            ActionKind.Click => $"click {El()}",
            ActionKind.TypeText => $"type_text {El()} \"{Truncate(a.Text ?? "", 60)}\"",
            ActionKind.PressKeys => $"press_keys \"{a.Keys}\"",
            ActionKind.Scroll => $"scroll {El()} {a.ScrollAmount}",
            ActionKind.OpenApp => $"open_app \"{a.Target}\"",
            _ => a.Kind.ToString(),
        };
    }

    private static string DescribeForUser(UiAction a, UiElementInfo? e, UiSnapshot s, Lang lang)
    {
        string name = e == null ? "" : (string.IsNullOrWhiteSpace(e.Name) ? e.ControlType : $"\"{Truncate(e.Name, 40)}\" ({e.ControlType})");
        string app = Short(s.WindowTitle, s.ProcessName);
        return a.Kind switch
        {
            ActionKind.Click => L(lang, $"Click {name} in {app}", $"Bấm {name} trong {app}"),
            ActionKind.TypeText => L(lang, $"Type \"{Truncate(a.Text ?? "", 80)}\" into {name}", $"Gõ \"{Truncate(a.Text ?? "", 80)}\" vào {name}"),
            ActionKind.PressKeys => L(lang, $"Press {a.Keys} in {app}", $"Nhấn {a.Keys} trong {app}"),
            ActionKind.Scroll => L(lang, $"Scroll {(a.ScrollAmount >= 0 ? "down" : "up")} in {name}", $"Cuộn {(a.ScrollAmount >= 0 ? "xuống" : "lên")} trong {name}"),
            ActionKind.OpenApp => L(lang, $"Open {a.Target}", $"Mở {a.Target}"),
            _ => a.Kind.ToString(),
        };
    }

    // ---- argument coercion (models send numbers as strings, "[5]", 5.0 ...) ----

    internal static int? ArgInt(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!args.TryGetProperty(n, out var v)) continue;
            switch (v.ValueKind)
            {
                case JsonValueKind.Number:
                    if (v.TryGetInt32(out var i)) return i;
                    if (v.TryGetDouble(out var d)) return (int)Math.Round(d);
                    break;
                case JsonValueKind.String:
                    var digits = new string((v.GetString() ?? "").Where(c => char.IsDigit(c) || c == '-').ToArray());
                    if (int.TryParse(digits, out var p)) return p;
                    break;
                case JsonValueKind.Array:
                    foreach (var x in v.EnumerateArray())
                        if (x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var xi)) return xi;
                    break;
            }
        }
        return null;
    }

    internal static string? ArgString(JsonElement args, params string[] names)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            if (!args.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return v.ToString();
        }
        return null;
    }

    /// <summary>Fallback when the model writes the call as text, e.g. &lt;tool_call&gt;{"name":"click","arguments":{"id":3}}&lt;/tool_call&gt;.</summary>
    internal static OllamaToolCall? TryParseToolCallFromText(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var bytes = Encoding.UTF8.GetBytes(content);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'{') continue;
            try
            {
                var reader = new Utf8JsonReader(bytes.AsSpan(i), new JsonReaderOptions { AllowTrailingCommas = true });
                using var doc = JsonDocument.ParseValue(ref reader);
                var call = OllamaClient.ParseToolCall(doc.RootElement);
                if (call != null && Tools.Any(t => t?["function"]?["name"]?.GetValue<string>() == call.Name.ToLowerInvariant()))
                    return call;
            }
            catch (JsonException) { }
        }
        return null;
    }
}
