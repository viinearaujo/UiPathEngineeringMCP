using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectJsonParserTests {
    private const string ProjectRoot = "/projects/testProcess";
    private const string ProjectJsonPath = "/projects/testProcess/project.json";

    private const string SampleProjectJson = """
    {
      "name": "testProcess",
      "description": "Blank Process",
      "main": "Main.xaml",
      "dependencies": {
        "UiPath.System.Activities": "22.10.4",
        "UiPath.Excel.Activities": "[2.11.4]"
      }
    }
    """;

    private static (ProjectJsonParser parser, FakeFilesystemProvider fs) CreateSut() {
        var fs = new FakeFilesystemProvider {
            ProjectJsonPath = ProjectJsonPath
        };
        fs.FileContents[ProjectJsonPath] = SampleProjectJson;
        fs.XamlFiles.Add($"{ProjectRoot}/Main.xaml");
        fs.XamlFiles.Add($"{ProjectRoot}/Sub/Process.xaml");
        return (new ProjectJsonParser(fs), fs);
    }

    [Fact]
    public void Parse_ReadsProjectNameMainWorkflowAndDescription() {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal("testProcess", model.ProjectName);
        Assert.Equal("Main.xaml", model.MainWorkflow);
        Assert.Equal("Blank Process", model.Description);
        Assert.Equal(ProjectRoot, model.ProjectPath);
        Assert.Equal(ProjectJsonPath, model.ProjectJsonPath);
    }

    [Fact]
    public void Parse_FormatsDependenciesAsNameAndVersion() {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal(2, model.Dependencies.Count);
        Assert.Contains("UiPath.System.Activities (22.10.4)", model.Dependencies);
        Assert.Contains("UiPath.Excel.Activities ([2.11.4])", model.Dependencies);
    }

    [Fact]
    public void Parse_MapsDependenciesToPackages() {
        var (parser, _) = CreateSut();

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal(2, model.Packages.Count);
        Assert.Contains(model.Packages, p => p.Id == "UiPath.System.Activities" && p.Version == "22.10.4");
        Assert.Contains(model.Packages, p => p.Id == "UiPath.Excel.Activities" && p.Version == "[2.11.4]");
    }

    [Fact]
    public void Parse_MissingOptionalFields_UsesSafeDefaults() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = ProjectJsonPath };
        fs.FileContents[ProjectJsonPath] = "{ }";
        var parser = new ProjectJsonParser(fs);

        var model = parser.Parse(ProjectJsonPath, ProjectRoot);

        Assert.Equal("Unknown", model.ProjectName);
        Assert.Null(model.MainWorkflow);
        Assert.Null(model.Description);
        Assert.Empty(model.Dependencies);
        Assert.Empty(model.Packages);
        Assert.Empty(model.Workflows);
    }
}
