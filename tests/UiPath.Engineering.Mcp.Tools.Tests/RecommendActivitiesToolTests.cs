using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class RecommendActivitiesToolTests {
    private const string ProjectPath = "/projects/testProcess";

    [Fact]
    public async Task RecommendActivities_EmptyQuery_InvalidArgument() {
        var fs = new FakeFilesystemProvider();
        var tool = new RecommendActivitiesTool(fs, TestCatalogs.Resolver(fs));

        var result = await tool.RecommendActivities("  ", ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
    }

    [Fact]
    public async Task RecommendActivities_FallbackCatalog_ReturnsAtMostFive() {
        var fs = new FakeFilesystemProvider {
            ProjectJsonContent = """{ "name": "P", "dependencies": { "UiPath.System.Activities": "[26.4.0]" } }"""
        };
        var tool = new RecommendActivitiesTool(fs, TestCatalogs.Resolver(fs));

        var result = await tool.RecommendActivities("log message", ProjectPath);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.True(data.GetProperty("activities").GetArrayLength() <= 5);
        Assert.Contains("LogMessage", data.GetProperty("activities").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task RecommendActivities_UsesDiscoveredActivity() {
        var fs = new FakeFilesystemProvider {
            ProjectJsonContent = """{ "name": "P", "dependencies": { "UiPath.Excel.Activities": "[3.5.0]" } }"""
        };
        var discovery = new FakeActivityDiscovery {
            Hits = [new DiscoveredActivity("ReadRangeX", "UiPath.Excel.Activities.Business.ReadRangeX",
                "UiPath.Excel.Activities", "3.5.0")]
        };
        var tool = new RecommendActivitiesTool(fs, TestCatalogs.Resolver(fs, discovery));

        var result = await tool.RecommendActivities("ReadRangeX", ProjectPath);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Equal("ReadRangeX", data.GetProperty("activities")[0].GetProperty("name").GetString());
        Assert.Equal("3.5.0", data.GetProperty("activities")[0].GetProperty("packageVersion").GetString());
    }
}
