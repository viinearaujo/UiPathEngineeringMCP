using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server;

internal static class CopilotToolSurface {
    private static readonly Lazy<HashSet<string>> ReadOnlyAnnotatedNames = new(DiscoverReadOnlyAnnotatedNames);

    public static void FilterListedTools(ListToolsResult result, string? toolSurface = null) {
        if (CopilotConnectorTools.IsReadOnlySurface(toolSurface)) {
            FilterReadOnlyListedTools(result);
            return;
        }

        var kept = CopilotConnectorTools.FilterNames(result.Tools.Select(t => t.Name));
        var allowed = new HashSet<string>(kept, StringComparer.Ordinal);
        ReplaceTools(result, result.Tools.Where(t => allowed.Contains(t.Name)));
    }

    public static CallToolResult? RejectIfHidden(string? name, string? toolSurface = null) {
        if (CopilotConnectorTools.IsReadOnlySurface(toolSurface)) {
            if (name is not null && ReadOnlyAnnotatedNames.Value.Contains(name)) {
                return null;
            }

            return Reject(
                $"Tool '{name}' is not on the ReadOnly surface. Set McpServer:ToolSurface to All (or CopilotDefault) to expose write/execution tools.");
        }

        if (CopilotConnectorTools.IsDefault(name)) {
            return null;
        }

        return Reject(
            $"Tool '{name}' is not on the Copilot default connector. Set McpServer:ToolSurface to All to expose the full server surface.");
    }

    private static void FilterReadOnlyListedTools(ListToolsResult result) {
        ReplaceTools(result, result.Tools.Where(IsReadOnlyListedTool));
    }

    private static bool IsReadOnlyListedTool(Tool tool) {
        if (tool.Annotations?.ReadOnlyHint is bool readOnly
            && tool.Annotations?.DestructiveHint is bool destructive) {
            return readOnly && !destructive;
        }

        return tool.Name is not null && ReadOnlyAnnotatedNames.Value.Contains(tool.Name);
    }

    private static void ReplaceTools(ListToolsResult result, IEnumerable<Tool> tools) {
        var list = tools.ToList();
        result.Tools.Clear();
        foreach (var tool in list) {
            result.Tools.Add(tool);
        }
    }

    private static CallToolResult Reject(string message) => new() {
        IsError = true,
        Content = [
            new TextContentBlock { Text = message }
        ]
    };

    private static HashSet<string> DiscoverReadOnlyAnnotatedNames() {
        var names = new HashSet<string>(StringComparer.Ordinal);
        Assembly assembly;
        try {
            assembly = Assembly.Load("UiPath.Engineering.Mcp.Tools");
        } catch (Exception) {
            return names;
        }

        foreach (var type in assembly.GetTypes()) {
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null || !attr.ReadOnly || attr.Destructive) {
                    continue;
                }

                names.Add(string.IsNullOrWhiteSpace(attr.Name)
                    ? JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name)
                    : attr.Name);
            }
        }

        return names;
    }
}
