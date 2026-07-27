using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectModelBuilderTests
{
    [Fact]
    public async Task BuildAsync_WhenProjectJsonMissing_ThrowsFileNotFound()
    {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = null };
        var builder = new ProjectModelBuilder(fs);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => builder.BuildAsync("/projects/missing"));
    }

    [Fact]
    public async Task BuildAsync_WhenProjectJsonPresent_ReturnsPopulatedModel()
    {
        const string root = "/projects/testProcess";
        const string json = "/projects/testProcess/project.json";

        var fs = new FakeFilesystemProvider { ProjectJsonPath = json };
        fs.FileContents[json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.XamlFiles.Add($"{root}/Main.xaml");

        var builder = new ProjectModelBuilder(fs);

        var model = await builder.BuildAsync(root);

        Assert.Equal("testProcess", model.ProjectName);
        Assert.Equal("Main.xaml", model.MainWorkflow);
        Assert.Contains("Main.xaml", model.Workflows);
    }
}
