using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

internal static class DocsGate {
    public static ToolError ToToolError(DocsFinding finding) =>
        new(finding.Code, finding.Message, finding.FixHint, finding.SuggestedTool);
}
