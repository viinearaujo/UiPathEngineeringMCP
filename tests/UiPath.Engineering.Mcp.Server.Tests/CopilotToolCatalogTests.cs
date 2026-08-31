using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotToolCatalogTests {
    [Fact]
    public void EveryRegisteredTool_IsDefaultOrDocumentedLeaveOff() {
        var catalog = ListMcpToolNames(Assembly.Load("UiPath.Engineering.Mcp.Tools"));
        var defaults = CopilotConnectorTools.DefaultNames;
        var leaveOff = CopilotConnectorTools.LeaveOffNames;

        Assert.Equal(defaults.Length, defaults.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(leaveOff.Length, leaveOff.Distinct(StringComparer.Ordinal).Count());

        foreach (var name in defaults) {
            Assert.DoesNotContain(name, leaveOff);
        }

        var documented = defaults.Concat(leaveOff).ToHashSet(StringComparer.Ordinal);
        var undocumented = catalog.Where(name => !documented.Contains(name)).ToArray();
        var staleLeaveOff = leaveOff.Where(name => !catalog.Contains(name, StringComparer.Ordinal)).ToArray();
        var missingDefaults = defaults.Where(name => !catalog.Contains(name, StringComparer.Ordinal)).ToArray();

        Assert.True(
            undocumented.Length == 0,
            "Every [McpServerTool] must be on DefaultNames or LeaveOffNames. Undocumented: "
            + string.Join(", ", undocumented));
        Assert.True(
            staleLeaveOff.Length == 0,
            "LeaveOffNames must match the real catalog. Stale: "
            + string.Join(", ", staleLeaveOff));
        Assert.True(
            missingDefaults.Length == 0,
            "DefaultNames must exist on the real catalog. Missing: "
            + string.Join(", ", missingDefaults));
        Assert.Equal(catalog.Count, documented.Count);
    }

    [Fact]
    public void OverlappingGatesAndXamlHatches_StayLeaveOff() {
        string[] leaveOffOverlaps = [
            "verify_work",
            "compile_project",
            "write_workflow_file",
            "edit_workflow_activity"
        ];
        foreach (var name in leaveOffOverlaps) {
            Assert.Contains(name, CopilotConnectorTools.LeaveOffNames);
            Assert.False(CopilotConnectorTools.IsDefault(name), name);
        }

        Assert.Contains("validate_project", CopilotConnectorTools.DefaultNames);
        Assert.Contains("update_plan_task", CopilotConnectorTools.DefaultNames);
        Assert.Contains("insert_activities", CopilotConnectorTools.DefaultNames);
    }

    internal static IReadOnlyList<string> ListMcpToolNames(Assembly assembly) {
        var names = new List<string>();
        foreach (var type in assembly.GetTypes()) {
            foreach (var method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)) {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is null) {
                    continue;
                }

                names.Add(string.IsNullOrWhiteSpace(attr.Name)
                    ? JsonNamingPolicy.SnakeCaseLower.ConvertName(method.Name)
                    : attr.Name);
            }
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
