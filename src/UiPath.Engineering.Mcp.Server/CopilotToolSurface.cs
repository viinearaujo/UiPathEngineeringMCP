using ModelContextProtocol.Protocol;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

internal static class CopilotToolSurface {
    public static void FilterListedTools(ListToolsResult result) {
        var kept = CopilotConnectorTools.FilterNames(result.Tools.Select(t => t.Name));
        var allowed = new HashSet<string>(kept, StringComparer.Ordinal);
        var filtered = result.Tools.Where(t => allowed.Contains(t.Name)).ToList();
        result.Tools.Clear();
        foreach (var tool in filtered) {
            result.Tools.Add(tool);
        }
    }

    public static CallToolResult? RejectIfHidden(string? name) {
        if (CopilotConnectorTools.IsDefault(name)) {
            return null;
        }

        return new CallToolResult {
            IsError = true,
            Content = [
                new TextContentBlock {
                    Text = $"Tool '{name}' is not on the Copilot default connector. Set McpServer:ToolSurface to All to expose the full server surface."
                }
            ]
        };
    }
}
