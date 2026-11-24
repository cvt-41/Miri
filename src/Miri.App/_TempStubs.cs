// Temporary stand-ins for Miri.Hands / Miri.Brain, compiled ONLY for isolated UI test builds:
//   dotnet build src/Miri.App/Miri.App.csproj --no-dependencies -p:DefineConstants=MIRI_STUBS
// The normal build uses the real types from the referenced projects.
#if MIRI_STUBS
#pragma warning disable CS0436 // stub types may shadow the referenced assemblies' types
using Miri.Core;

namespace Miri.Hands
{
    public sealed class UiaScreenReader : IScreenReader, IDisposable
    {
        public UiSnapshot? SnapshotForeground(IntPtr excludeHwnd) => null;
        public IReadOnlyList<(IntPtr Hwnd, string Title, string Process)> ListWindows() => Array.Empty<(IntPtr, string, string)>();
        public void Dispose() { }
    }

    public sealed class UiaActionExecutor : IActionExecutor
    {
        public UiaActionExecutor(MiriSettings settings, UiaScreenReader reader) { }
        public ActionRisk Assess(UiAction action, UiSnapshot snapshot) => new(RiskLevel.Normal, "stub");
        public Task<ActionResult> ExecuteAsync(UiAction action, UiSnapshot snapshot, CancellationToken ct) =>
            Task.FromResult(new ActionResult(false, "stub executor"));
    }
}

namespace Miri.Brain
{
    public sealed class MiriBrain : IMiriBrain
    {
        private readonly IMiriEvents _events;
        private readonly IApprovalGate _approval;

        public MiriBrain(MiriSettings settings, IScreenReader reader, IActionExecutor executor,
                         IApprovalGate approval, IMiriEvents events, string knowledgeDir)
        {
            _events = events;
            _approval = approval;
            ActiveModel = settings.ChatModel;
        }

        public IntPtr OwnWindow { get; set; }
        public string ActiveModel { get; set; }

        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> AvailableModelsAsync() =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3.5:4b", "gemma4:e4b", "qwen3:4b" });

        public async Task HandleAsync(string userText, CancellationToken ct)
        {
            _events.OnStatus("stub brain");
            _events.OnMood(PetMood.Thinking);
            await Task.Delay(600, ct);
            if (userText.Contains("approve", StringComparison.OrdinalIgnoreCase))
            {
                var d = await _approval.RequestAsync(new ApprovalRequest(
                    new UiAction(ActionKind.Click, 1), "Click \"Send\" in Notepad?",
                    new ActionRisk(RiskLevel.Dangerous, "it sends a message"),
                    new System.Drawing.Rectangle(400, 300, 120, 40)), ct);
                _events.OnToken($"You said {d}. ");
            }
            _events.OnMood(PetMood.Talking);
            foreach (var w in ("Meow. I am only a stub, but I heard: " + userText).Split(' '))
            {
                ct.ThrowIfCancellationRequested();
                _events.OnToken(w + " ");
                await Task.Delay(60, ct);
            }
            _events.OnMood(PetMood.Happy);
        }
    }
}
#endif
