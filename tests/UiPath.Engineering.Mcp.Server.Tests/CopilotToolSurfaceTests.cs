using ModelContextProtocol.Protocol;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotToolSurfaceTests {
    [Fact]
    public void FilterListedTools_RemovesLeaveOffTools() {
        var result = new ListToolsResult {
            Tools = [
                new Tool { Name = "analyze_project" },
                new Tool { Name = "verify_work" },
                new Tool { Name = "search_repository" },
                new Tool { Name = "add_coded_workflow" }
            ]
        };

        CopilotToolSurface.FilterListedTools(result);

        Assert.Equal(["analyze_project", "add_coded_workflow"], result.Tools.Select(t => t.Name));
    }

    [Fact]
    public void RejectIfHidden_AllowsDefaultAndRejectsLeaveOff() {
        Assert.Null(CopilotToolSurface.RejectIfHidden("validate_project"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("add_coded_workflow"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("check_work"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("get_job"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("manage_project_content"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("navigate_code"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("search_knowledge"));
        Assert.Null(CopilotToolSurface.RejectIfHidden("checkpoint"));
        var rejected = CopilotToolSurface.RejectIfHidden("write_workflow_file");
        Assert.NotNull(rejected);
        Assert.True(rejected.IsError);
        Assert.NotNull(CopilotToolSurface.RejectIfHidden("get_compile_errors"));
        Assert.NotNull(CopilotToolSurface.RejectIfHidden("recommend_activities"));
    }

    [Fact]
    public void FilterListedTools_ReadOnly_KeepsAnnotationGatedReads() {
        var result = new ListToolsResult {
            Tools = [
                new Tool {
                    Name = "analyze_project",
                    Annotations = new ToolAnnotations { ReadOnlyHint = true, DestructiveHint = false }
                },
                new Tool {
                    Name = "edit_workflow_file",
                    Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }
                },
                new Tool {
                    Name = "run_workflow",
                    Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true }
                }
            ]
        };

        CopilotToolSurface.FilterListedTools(result, CopilotConnectorTools.SurfaceReadOnly);

        Assert.Equal(["analyze_project"], result.Tools.Select(t => t.Name));
    }

    [Fact]
    public void RejectIfHidden_ReadOnly_RejectsWrites() {
        _ = typeof(UiPath.Engineering.Mcp.Tools.AnalyzeProjectTool);
        Assert.Null(CopilotToolSurface.RejectIfHidden("analyze_project", CopilotConnectorTools.SurfaceReadOnly));
        var rejected = CopilotToolSurface.RejectIfHidden("edit_workflow_file", CopilotConnectorTools.SurfaceReadOnly);
        Assert.NotNull(rejected);
        Assert.True(rejected.IsError);
        Assert.Contains("ReadOnly", ((TextContentBlock)rejected.Content![0]).Text, StringComparison.Ordinal);
    }
}
