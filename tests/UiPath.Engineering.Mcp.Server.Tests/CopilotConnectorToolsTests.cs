using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotConnectorToolsTests {
    [Fact]
    public void DefaultNames_FitsMaxDefaultCountAndIsCodedFirst() {
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
            "analyze_project_gaps",
            "read_skill",
            "explain_workflow",
            "get_workflow_dependencies",
            "generate_documentation",
            "find_code_symbol",
            "find_code_references",
            "get_code_context",
            "validate_activity_spec",
            "recommend_activities",
            "build_workflow",
            "manage_workflow_data",
            "add_xaml_workflow",
            "create_implementation_plan",
            "create_project",
            "patch_project_json",
            "manage_project_docs",
            "manage_project_file",
            "sync_project_context",
            "validate_project_docs",
            "get_analyzer_rules",
            "manage_packages",
            "get_object_repository",
            "get_activity_metadata",
            "search_activity_docs",
            "search_uipath_knowledge",
        }, CopilotConnectorTools.DefaultNames);
        Assert.Contains("recommend_activities", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("list_skills", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("write_workflow_file", CopilotConnectorTools.DefaultNames);
        Assert.DoesNotContain("edit_workflow_activity", CopilotConnectorTools.DefaultNames);
        Assert.Equal(CopilotConnectorTools.DefaultNames.Length, CopilotConnectorTools.DefaultNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(string.Join(", ", CopilotConnectorTools.DefaultNames), CopilotConnectorTools.JoinDefaultNames());
    }

    [Fact]
    public void LeaveOffNames_AreHatchesAndAliasesOnly() {
        Assert.Equal(new[] {
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
        Assert.False(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceAll));
    }
}
