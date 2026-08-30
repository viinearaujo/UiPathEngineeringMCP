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
                new Tool { Name = "recommend_activities" }
            ]
        };

        CopilotToolSurface.FilterListedTools(result);

        Assert.Equal(["analyze_project", "recommend_activities"], result.Tools.Select(t => t.Name));
    }

    [Fact]
    public void RejectIfHidden_AllowsDefaultAndRejectsLeaveOff() {
        Assert.Null(CopilotToolSurface.RejectIfHidden("validate_project"));
        var rejected = CopilotToolSurface.RejectIfHidden("compile_project");
        Assert.NotNull(rejected);
        Assert.True(rejected.IsError);
    }
}
