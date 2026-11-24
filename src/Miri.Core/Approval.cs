namespace Miri.Core;

// ---- approval (implemented by Miri.App UI) ----
public enum ApprovalDecision { Yes, No, YesToAllForTask }

public sealed record ApprovalRequest(UiAction Action, string Description, ActionRisk Risk,
    System.Drawing.Rectangle? TargetBounds);

public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}
