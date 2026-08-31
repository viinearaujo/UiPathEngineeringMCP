using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CopilotConnectorToolsTests {
    [Fact]
    public void DefaultNames_IsAtMostTwelveAndIsCodedFirst() {
        Assert.True(CopilotConnectorTools.DefaultNames.Length <= CopilotConnectorTools.MaxDefaultCount);
        Assert.Equal(new[] {
            "analyze_project",
            "search_codebase",
            "read_workflow_file",
            "validate_project",
            "get_implementation_plan",
            "update_plan_task",
            "add_coded_workflow",
            "edit_workflow_file",
            "find_activity",
            "insert_activities",
            "get_compile_errors",
        }, CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("recommend_activities", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("write_workflow_file", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("edit_workflow_activity", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("compile_project", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("verify_work", CopilotConnectorTools.DefaultNames);
        Assert.Equal(CopilotConnectorTools.DefaultNames.Length, CopilotConnectorTools.DefaultNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void JoinDefaultNames_MatchesArrayOrder() {
        Assert.Equal(string.Join(", ", CopilotConnectorTools.DefaultNames), CopilotConnectorTools.JoinDefaultNames());
        Assert.Equal(
            string.Join(", ", CopilotConnectorTools.DefaultNames.Select(n => $"`{n}`")),
            CopilotConnectorTools.JoinDefaultNamesMarkdown());
    }

    [Fact]
    public void LeaveOffNames_AreNotOnTheDefaultConnector() {
        foreach (var name in CopilotConnectorTools.LeaveOffNames) {
            Assert.False(CopilotConnectorTools.IsDefault(name), name);
        }
    }

    [Fact]
    public void FilterNames_KeepsOnlyDefaultTools() {
        var mixed = CopilotConnectorTools.DefaultNames.Concat(CopilotConnectorTools.LeaveOffNames);
        var filtered = CopilotConnectorTools.FilterNames(mixed);
        Assert.Equal(CopilotConnectorTools.DefaultNames, filtered);
    }

    [Fact]
    public void RestrictsSurface_OnlyAllDisablesTheFilter() {
        Assert.True(CopilotConnectorTools.RestrictsSurface(null));
        Assert.True(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceCopilotDefault));
        Assert.True(CopilotConnectorTools.RestrictsSurface("unexpected"));
        Assert.False(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceAll));
        Assert.False(CopilotConnectorTools.RestrictsSurface("all"));
    }
}
