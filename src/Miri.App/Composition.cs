using System.IO;
using Miri.Core;

namespace Miri.App;

/// <summary>
/// The ONLY place that constructs Miri.Hands / Miri.Brain types. If their constructors change,
/// fix them up here.
/// </summary>
internal static class Composition
{
    public sealed record Parts(IMiriBrain Brain, IReadOnlyList<IDisposable> Disposables);

    /// <param name="ownWindow">HWND of the bubble window (the brain skips it when snapshotting the foreground).</param>
    public static Parts Create(MiriSettings settings, IApprovalGate approval, IMiriEvents events, IntPtr ownWindow)
    {
        var reader = new Miri.Hands.UiaScreenReader();
        var executor = new Miri.Hands.UiaActionExecutor(settings, reader) { BeforeTouch = events.PointAtAsync };   // kitten hops onto what Miri presses
        string knowledgeDir = Path.Combine(AppContext.BaseDirectory, "knowledge");
        var brain = new Miri.Brain.MiriBrain(settings, reader, executor, approval, events, knowledgeDir)
        {
            OwnWindow = ownWindow,
        };

        // No warm-up: models load only when the user asks something, and unload shortly after (saves RAM).

        var disposables = new List<IDisposable>();
        if ((object)brain is IDisposable b) disposables.Add(b);
        if ((object)executor is IDisposable e) disposables.Add(e);
        disposables.Add(reader);
        return new Parts(brain, disposables);
    }
}
