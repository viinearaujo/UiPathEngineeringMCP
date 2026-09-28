using UiPath.Engineering.Mcp.Core.Planning;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class CheckWorkToolTests {
    [Fact]
    public async Task CheckWork_WhenPlanIsCorrupt_ReturnsPlanInvalid() {
        const string projectPath = "/projects/testProcess";
        var fs = new FakeFilesystemProvider { Allowed = true, ProjectJson = projectPath + "/project.json" };
        fs.FileContents[ImplementationPlanStore.GetJsonPath(projectPath)] = "{not-json";
        var tool = new CheckWorkTool(
            fs,
            new FakeCSharpAnalysisService(),
            new FakeUiPathCliProvider(),
            new FakeProjectModelBuilder(),
            new ImplementationPlanStore(fs),
            DocsSupport.Validator(fs));

        var result = await tool.CheckWork(projectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains("PLAN_INVALID", result.Errors[0]);
    }
}
