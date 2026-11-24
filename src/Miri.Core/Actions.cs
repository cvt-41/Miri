namespace Miri.Core;

// ---- actions ----
public enum ActionKind { Click, TypeText, PressKeys, Scroll, FocusWindow, OpenApp }
public enum RiskLevel { Normal, Dangerous, Blocked }

public sealed record UiAction(ActionKind Kind, int? ElementId = null, string? Text = null,
    string? Keys = null, int ScrollAmount = 0, string? Target = null);
public sealed record ActionRisk(RiskLevel Level, string Reason);
public sealed record ActionResult(bool Success, string Message);

public interface IScreenReader            // implemented by Miri.Hands
{
    UiSnapshot? SnapshotForeground(IntPtr excludeHwnd);   // skip Miri's own windows
    /// <summary>Like SnapshotForeground but without the 60-element cap (for matching, never sent to the model).</summary>
    UiSnapshot? SnapshotForegroundAll(IntPtr excludeHwnd) => SnapshotForeground(excludeHwnd);
    IReadOnlyList<(IntPtr Hwnd, string Title, string Process)> ListWindows();
}

public interface IActionExecutor          // implemented by Miri.Hands
{
    ActionRisk Assess(UiAction action, UiSnapshot snapshot);
    Task<ActionResult> ExecuteAsync(UiAction action, UiSnapshot snapshot, CancellationToken ct);
}
