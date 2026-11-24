# Miri — implementation spec (v1)

Source of truth for the build agents. Read `PLAN.md` for the product vision; this file fixes the
technical contracts so projects can be built in parallel.

## Toolchain
- .NET SDK 9.0.321 (installed). Target **`net9.0-windows`**, `<UseWPF>true</UseWPF>`, `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`.
- If `dotnet` is not on PATH in your shell, use `"C:\Program Files\dotnet\dotnet.exe"`.
- NuGet allowed: `FlaUI.UIA3` (Hands), `Microsoft.Data.Sqlite` (Brain). Nothing else without asking.
- Ollama at `http://127.0.0.1:11434`. Installed models: `qwen3.5:4b` (default; text+vision+tools), `gemma4:e4b` (alternate; text+vision+tools; may still be downloading), `qwen3:4b` (fallback; text+tools only).

## Solution layout (repo root)
```
Miri.sln
src/Miri.Core/     class library  net9.0-windows  — contracts + settings ONLY (no logic beyond DTOs)
src/Miri.Brain/    class library  net9.0-windows  — refs Core. Ollama, router, persona, knowledge, memory, agent loop
src/Miri.Hands/    class library  net9.0-windows  — refs Core. FlaUI UIA3 snapshot + actions + safety policy
src/Miri.Pet/      class library  net9.0-windows  UseWPF — refs Core. sprite sheet generation + animation + behaviour
src/Miri.App/      WinExe         net9.0-windows  UseWPF — refs all. windows, bubble, tray, hotkeys, wiring
knowledge/coffee/*.md, knowledge/cats/*.md   — copied to output (Content, PreserveNewest) by Miri.App
```
Data dir at runtime: `%LOCALAPPDATA%\Miri\` (settings.json, memory.db, actions.log).

## Miri.Core contracts (exact)
```csharp
namespace Miri.Core;

public sealed class MiriSettings {
    public string OllamaUrl { get; set; } = "http://127.0.0.1:11434";
    public string ChatModel { get; set; } = "qwen3.5:4b";
    public string AlternateModel { get; set; } = "gemma4:e4b";
    public string FallbackModel { get; set; } = "qwen3:4b";
    public int MaxAgentSteps { get; set; } = 8;
    public List<string> BlockedApps { get; set; } = new() { "KeePass", "Bitwarden", "1Password" };
    public string ChatHotkey { get; set; } = "Ctrl+Alt+K";   // was Ctrl+Alt+Space (clashed with other apps; migrated on Load)
    public string StopHotkey { get; set; } = "Ctrl+Alt+M";
    public int Scale { get; set; } = 3;           // sprite pixel scale
    public static string DataDir { get; }          // %LOCALAPPDATA%\Miri, created on access
    public static MiriSettings Load();             // DataDir\settings.json, defaults if missing/corrupt
    public void Save();
}

// ---- UI snapshot ----
public sealed record UiElementInfo(int Id, string ControlType, string Name, string? Value,
    System.Drawing.Rectangle Bounds, bool IsPassword, bool IsEnabled);
public sealed record UiSnapshot(string WindowTitle, string ProcessName, IntPtr Hwnd,
    System.Drawing.Rectangle WindowBounds, IReadOnlyList<UiElementInfo> Elements) {
    public string ToPromptText();   // 'Active window: "<title>" (<process>)\n[1] Button "Send"\n...' max ~60 lines
}

// ---- actions ----
public enum ActionKind { Click, TypeText, PressKeys, Scroll, FocusWindow, OpenApp }
public enum RiskLevel { Normal, Dangerous, Blocked }
public sealed record UiAction(ActionKind Kind, int? ElementId = null, string? Text = null,
    string? Keys = null, int ScrollAmount = 0, string? Target = null);
public sealed record ActionRisk(RiskLevel Level, string Reason);
public sealed record ActionResult(bool Success, string Message);

public interface IScreenReader {            // implemented by Miri.Hands
    UiSnapshot? SnapshotForeground(IntPtr excludeHwnd);   // skip Miri's own windows
    IReadOnlyList<(IntPtr Hwnd, string Title, string Process)> ListWindows();
}
public interface IActionExecutor {          // implemented by Miri.Hands
    ActionRisk Assess(UiAction action, UiSnapshot snapshot);
    Task<ActionResult> ExecuteAsync(UiAction action, UiSnapshot snapshot, CancellationToken ct);
}

// ---- approval (implemented by Miri.App UI) ----
public enum ApprovalDecision { Yes, No, YesToAllForTask }
public sealed record ApprovalRequest(UiAction Action, string Description, ActionRisk Risk,
    System.Drawing.Rectangle? TargetBounds);
public interface IApprovalGate {
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

// ---- brain-facing (implemented by Miri.Brain) ----
public enum Intent { Question, ScreenTask, ScreenQuestion, Chitchat }
public enum PetMood { Idle, Thinking, Talking, Happy, Confused, Pointing }
public interface IMiriEvents {               // implemented by Miri.App; Brain calls it
    void OnToken(string text);              // streaming reply text
    void OnMood(PetMood mood);
    void OnPointAt(System.Drawing.Rectangle screenBounds);   // kitten walks/points there
    void OnStatus(string text);             // short status ("Looking at Telegram...")
}
public interface IMiriBrain {
    Task HandleAsync(string userText, CancellationToken ct);  // routes + answers/acts, streaming via IMiriEvents
    Task<IReadOnlyList<string>> AvailableModelsAsync();
    string ActiveModel { get; set; }
}
```
(`System.Drawing.Rectangle` requires `<UseWindowsForms>`-free reference: add `System.Drawing.Primitives` — it is in the shared framework; plain `using System.Drawing;` works on net9.0-windows.)

## Miri.Brain
- `OllamaClient`: `/api/chat` streaming NDJSON, `think:false` for chat, optional `tools`, optional `images` (base64). `/api/tags` for model list. If `ChatModel` is missing, fall back AlternateModel → FallbackModel and report via OnStatus. Timeout ≥ 10 min (CPU is slow).
- `Persona`: system prompt — Miri is a calm, slightly grumpy but helpful white pixel cat; answers < 120 words; **always replies in the language the user wrote in (Vietnamese or English)**; never invents facts — say "I'm not sure, meow" instead.
- `IntentRouter`: cheap heuristic first (VI+EN keywords: click/bấm/nhấn, type/gõ/nhập, open/mở, "on my screen"/"trên màn hình", "this button"/"nút này"), else one short LLM classification call returning one word.
- `KnowledgeBase`: loads `knowledge/**/*.md` (dir next to exe), splits by `##` headings, keyword/BM25-style scoring with Vietnamese diacritics stripped for matching; top 3 chunks injected into the prompt. Interface `IKnowledgeSearch` internal so embeddings (`bge-m3`) can be added later.
- `MemoryStore` (SQLite `memory.db`): table `facts(id, text, created)`. After each Question answer, a tiny LLM call extracts a durable user preference if any ("NONE" otherwise) and stores it. Last 10 facts are injected into prompts. Also keep last 6 chat turns in RAM for context.
- `AgentLoop` for ScreenTask: snapshot (IScreenReader) → prompt with `ToPromptText()` + tools `click(id)`, `type_text(id,text)`, `press_keys(keys)`, `scroll(id,amount)`, `open_app(name)`, `done(summary)` → parse first tool call → `Assess` → if Blocked: tell the user and stop; else `OnPointAt(bounds)` + `IApprovalGate.RequestAsync` (Dangerous never auto-approved by YesToAll) → execute → re-snapshot → loop ≤ MaxAgentSteps. Cancellation via CancellationToken (stop hotkey).
- ScreenQuestion: snapshot → answer about the UI; if the model mentions an element id, `OnPointAt` it. If snapshot has < 3 elements, send a screenshot of the window to the vision-capable model instead (capture with `System.Drawing` `Graphics.CopyFromScreen` — window bounds only, never full desktop).
- Constructor: `MiriBrain(MiriSettings, IScreenReader, IActionExecutor, IApprovalGate, IMiriEvents, string knowledgeDir)`.

## Miri.Hands (FlaUI.UIA3)
- `UiaScreenReader : IScreenReader`: foreground window via `GetForegroundWindow`; if it's `excludeHwnd`, use the next top-level visible window in Z-order. Walk descendants (cap 400 nodes, 1.5 s budget), keep elements that are on-screen, enabled-or-text, and of interactive/text types (Button, Edit, Hyperlink, ListItem, MenuItem, TabItem, CheckBox, RadioButton, ComboBox, TreeItem, Text with name). Dedupe, sort top-to-bottom/left-to-right, keep ≤ 60, assign Ids 1..N. Keep an internal map Id→AutomationElement for the latest snapshot.
- `UiaActionExecutor : IActionExecutor`: Click (Invoke pattern, else mouse click at center), TypeText (Value pattern, else focus + `Keyboard.Type`), PressKeys (parse "ctrl+s" etc.), Scroll, FocusWindow, OpenApp (Start-menu style: `Process.Start` with shell execute on a name; only for names, never URLs or paths from model output containing `\\` or `:`).
- `SafetyPolicy.Assess`: **Blocked** if target `IsPassword`, process/title matches BlockedApps, banking keywords in title (bank, ngân hàng, vietcombank, techcombank, momo, paypal, wallet), or text looks like a credential (≥ 12 chars random-looking with digits+symbols). **Dangerous** if element name/text/keys contain send/gửi, delete/xóa/xoá, buy/mua, pay/thanh toán, post/đăng, submit, confirm/xác nhận, or keys are `enter` in a messaging app / `ctrl+enter` / `alt+f4`. Else Normal.
- Append every executed action to `DataDir\actions.log` (timestamp, window, action, result).
- Elevated windows: catch access errors → `ActionResult(false, "That window runs as admin, I can't touch it")`.

## Miri.Pet (WPF)
- `SpriteFactory`: **generates the sprite sheet procedurally in code** (no external art needed) as `WriteableBitmap` frames, 32×32 each, from ASCII pixel maps defined in C#. Palette: fur `#F4F4F2`, shade `#C9CDD6`, outline `#5A5E6B`, eyes `#7ED957` (rim `#2F7A3B`, sparkle `#FFFFFF`), pupil `#1E1E1E`, nose/inner-ear `#E8A9A6`, transparent background. Look: white cat sitting front-facing, pointed ears with pink insides, big round green eyes with a sparkle (cute), small pink nose, a few whisker pixels. Also export the sheet to `assets/sprites/miri.png` on first run if missing (so the user can redraw it in Aseprite; if a `miri.png` + `miri.json` exist next to the exe, load those instead).
- Animations (frame lists + fps): `Sit` (4f, blink + tail flick), `Walk` (4f, side view, mirrored for left), `Sleep` (2f, curled, "z"), `Think` (2f, ear twitch), `Talk` (2f, mouth), `Happy` (2f, slow blink), `Confused` (2f, head tilt), `Point` (2f, paw raised).
- `PetBehaviour`: state machine on a 30 fps `DispatcherTimer` (or CompositionTarget.Rendering): idle → wander along "floor" surfaces → sit → sleep after 2 min of no user input (`GetLastInputInfo`). Floors = top edge of the taskbar work area + top edges of visible top-level window title bars (EnumWindows, refresh every 500 ms). Gravity when the floor under it disappears. Drag with mouse (picked-up pose = Confused frames). `WalkTo(Point screen)` for pointing. Deaf-cat gag: never reacts to sounds; perks up (Think frame) when the user types nearby (input activity).
- Expose `PetView : FrameworkElement`/`Image` + `PetController` with `SetMood(PetMood)`, `WalkTo(Point)`, `Position` and `event Clicked`.

## Miri.App (WPF)
- `PetWindow`: borderless, `AllowsTransparency=True`, `Background=Transparent`, `Topmost`, `ShowInTaskbar=False`, sized to sprite × Scale, `RenderOptions.BitmapScalingMode=NearestNeighbor`. Click-through on transparent pixels (WM_NCHITTEST → HTTRANSPARENT where alpha==0). Hidden from Alt-Tab (WS_EX_TOOLWINDOW).
- `BubbleWindow`: speech bubble anchored above the kitten — pixel-styled border, TextBox input (Enter sends, Esc closes), streaming reply text, approval card UI with **[Yes] [No] [Yes to all for this task]** buttons; Dangerous = red border + no "Yes to all". Opened by clicking the kitten or ChatHotkey (RegisterHotKey).
- `HighlightWindow`: transparent click-through overlay drawing a pulsing rectangle around the approval target.
- Tray icon: use `System.Windows.Forms.NotifyIcon` (set `<UseWindowsForms>true</UseWindowsForms>` in Miri.App only; beware `Application`/`MessageBox` ambiguity — alias WPF types). Menu: Model ▸ (list from AvailableModelsAsync, radio-checked, persisted to settings), Pause/Resume pet, Open data folder, Quit.
- StopHotkey cancels the current CancellationTokenSource.
- Implements `IApprovalGate`, `IMiriEvents` (marshal to Dispatcher).
- Single-instance (named Mutex).

## Done = 
`dotnet build Miri.sln -c Debug` has 0 errors; `dotnet run --project src/Miri.App` shows the kitten on the taskbar, clicking it opens the bubble, a question gets a streamed answer from Ollama.
