using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectJsonParserTests
{
    private const string ProjectRoot = "/projects/testProcess";
    private const string ProjectJsonPath = "/projects/testProcess/project.json";

    private const string SampleProjectJson = """
    {
      "name": "testProcess",
      "main": "Main.xaml",
      "dependencies": {
        "UiPath.System.Activities": "22.10.4",
        "UiPath.Excel.Activities": "[2.11.4]"
      }
    }
    """;

    private static (ProjectJsonParser parser, FakeFilesystemProvider fs) CreateSut()
    {
        var fs = new FakeFilesystemProvider
        {
            ProjectJsonPath = ProjectJsonPath
        };
        fs.FileContents[ProjectJsonPath] = SampleProjectJson;
        fs.XamlFiles.Add($"{ProjectRoot}/Main.xaml");
        fs.XamlFiles.Add($"{ProjectRoot}/Sub/Process.xaml");
        return (new ProjectJsonParser(fs), fs);
    }

    [Fact]
    public void Parse_ReadsProjectNameAndMainWorkflow()
    {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal("testProcess", model.ProjectName);
        Assert.Equal("Main.xaml", model.MainWorkflow);
        Assert.Equal(ProjectRoot, model.ProjectPath);
        Assert.Equal(ProjectJsonPath, model.ProjectJsonPath);
    }

    [Fact]
    public void Parse_FormatsDependenciesAsNameAndVersion()
    {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal(2, model.Dependencies.Count);
        Assert.Contains("UiPath.System.Activities (22.10.4)", model.Dependencies);
        Assert.Contains("UiPath.Excel.Activities ([2.11.4])", model.Dependencies);
    }

    [Fact]
    public void Parse_ReturnsWorkflowFileNamesOnly()
    {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal(2, model.Workflows.Count);
        Assert.Contains("Main.xaml", model.Workflows);
        Assert.Contains("Process.xaml", model.Workflows);
        // Full paths should have been reduced to file names.
        Assert.DoesNotContain(model.Workflows, w => w.Contains('/'));
    }

    [Fact]
    public void Parse_MissingOptionalFields_UsesSafeDefaults()
    {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = ProjectJsonPath };
        fs.FileContents[ProjectJsonPath] = "{ }";
        var parser = new ProjectJsonParser(fs);

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal("Unknown", model.ProjectName);
        Assert.Null(model.MainWorkflow);
        Assert.Empty(model.Dependencies);
        Assert.Empty(model.Workflows);
    }
}
