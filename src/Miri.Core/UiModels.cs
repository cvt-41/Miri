using System.Text;

namespace Miri.Core;

// ---- UI snapshot ----
public sealed record UiElementInfo(int Id, string ControlType, string Name, string? Value,
    System.Drawing.Rectangle Bounds, bool IsPassword, bool IsEnabled);

public sealed record UiSnapshot(string WindowTitle, string ProcessName, IntPtr Hwnd,
    System.Drawing.Rectangle WindowBounds, IReadOnlyList<UiElementInfo> Elements)
{
    private const int MaxElementLines = 60;
    private const int MaxValueLength = 40;

    /// <summary>'Active window: "&lt;title&gt;" (&lt;process&gt;)\n[1] Button "Send"\n...' (max 60 element lines).</summary>
    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.Append("Active window: \"").Append(WindowTitle).Append("\" (").Append(ProcessName).Append(')');
        foreach (var e in Elements.Take(MaxElementLines))
        {
            sb.Append('\n');
            sb.Append('[').Append(e.Id).Append("] ").Append(e.ControlType).Append(" \"").Append(e.Name).Append('"');
            if (!string.IsNullOrEmpty(e.Value))
            {
                var v = e.Value.Length > MaxValueLength ? e.Value[..MaxValueLength] + "..." : e.Value;
                sb.Append(" = \"").Append(v).Append('"');
            }
        }
        return sb.ToString();
    }
}
