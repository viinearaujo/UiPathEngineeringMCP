using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotConnectorToolsTests {
    [Fact]
    public void DefaultNames_FitsMaxDefaultCountAndIsCodedFirst() {
        Assert.True(CopilotConnectorTools.DefaultNames.Length <= CopilotConnectorTools.MaxDefaultCount);
        Assert.Equal(26, CopilotConnectorTools.DefaultNames.Length);
        Assert.Equal(new[] {
            "analyze_project",
            "search_codebase",
            "read_workflow_file",
            "explain_workflow",
            "get_workflow_dependencies",
            "validate_project",
            "analyze_project_gaps",
            "check_work",
            "get_job",
            "navigate_code",
            "create_implementation_plan",
            "get_implementation_plan",
            "update_plan_task",
            "add_coded_workflow",
            "edit_workflow_file",
            "find_activity",
            "insert_activities",
            "validate_activity_spec",
            "build_workflow",
            "manage_workflow_data",
            "search_knowledge",
            "manage_project_content",
            "checkpoint",
            "get_changes",
            "revert_changes",
            "update_canvas_snapshot",
        }, CopilotConnectorTools.DefaultNames);
        Assert.Contains("check_work", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("list_skills", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("write_workflow_file", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("edit_workflow_activity", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("generate_documentation", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("find_code_symbol", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("read_skill", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("manage_project_docs", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("get_compile_errors", CopilotConnectorTools.DefaultNames);
        Assert.Equal(CopilotConnectorTools.DefaultNames.Length, CopilotConnectorTools.DefaultNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(string.Join(", ", CopilotConnectorTools.DefaultNames), CopilotConnectorTools.JoinDefaultNames());
    }

    [Fact]
    public void LeaveOffNames_AreHatchesAndAliasesOnly() {
        Assert.Equal(new[] {
            "generate_documentation",
            "create_project",
            "add_xaml_workflow",
            "patch_project_json",
            "get_analyzer_rules",
            "write_workflow_file",
            "edit_workflow_activity",
            "compile_project",
            "verify_work",
            "run_ui_path_cli",
            "list_skills",
            "search_repository",
            "create_work_items",
            "run_workflow",
            "control_debug_session",
            "get_compile_errors",
            "recommend_activities",
            "manage_packages",
            "get_object_repository",
            "get_activity_metadata",
        }, CopilotConnectorTools.LeaveOffNames);
        foreach (var name in CopilotConnectorTools.LeaveOffNames) {
            Assert.False(CopilotConnectorTools.IsDefault(name), name);
        }
    }

    [Fact]
    public void FilterNames_KeepsOnlyDefaultTools() {
        var mixed = CopilotConnectorTools.DefaultNames.Concat(CopilotConnectorTools.LeaveOffNames);
        Assert.Equal(CopilotConnectorTools.DefaultNames, CopilotConnectorTools.FilterNames(mixed));
    }

    [Fact]
    public void RestrictsSurface_OnlyAllDisablesTheFilter() {
        Assert.True(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceCopilotDefault));
        Assert.True(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceReadOnly));
        Assert.False(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceAll));
        Assert.True(CopilotConnectorTools.IsReadOnlySurface(CopilotConnectorTools.SurfaceReadOnly));
    }
}
