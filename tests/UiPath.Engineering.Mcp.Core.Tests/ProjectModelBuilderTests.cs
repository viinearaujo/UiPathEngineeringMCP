using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectModelBuilderTests {
    private const string Root = "/projects/testProcess";
    private const string Json = "/projects/testProcess/project.json";

    private const string MinimalXaml = """
    <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:ui="http://schemas.uipath.com/workflow/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
      <Sequence DisplayName="Main Sequence">
        <ui:InvokeWorkflowFile DisplayName="Invoke Sub" WorkflowFileName="Sub.xaml" />
      </Sequence>
    </Activity>
    """;

    [Fact]
    public async Task BuildAsync_WhenProjectJsonMissing_ThrowsFileNotFound() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = null };
        var builder = new ProjectModelBuilder(fs);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => builder.BuildAsync("/projects/missing"));
    }

    [Fact]
    public async Task BuildAsync_WhenProjectJsonPresent_ReturnsPopulatedModel() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.XamlFiles.Add($"{Root}/Main.xaml");
        fs.FileContents[$"{Root}/Main.xaml"] = MinimalXaml;

        var builder = new ProjectModelBuilder(fs);

        var model = await builder.BuildAsync(Root);

        Assert.Equal("testProcess", model.ProjectName);
        Assert.Equal("Main.xaml", model.MainWorkflow);
        var workflow = Assert.Single(model.Workflows);
        Assert.Equal("Main.xaml", workflow.FileName);
        Assert.True(workflow.IsMain);
        Assert.False(workflow.HasParseError);
    }

    [Fact]
    public async Task BuildAsync_ReadsReadmeSummaryWhenPresent() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.FileContents[$"{Root}/README.md"] = "  # Test Process\n\nDoes things.  ";

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        Assert.Equal("# Test Process\n\nDoes things.", model.ReadmeSummary);
    }

    [Fact]
    public async Task BuildAsync_MissingReadme_LeavesSummaryNull() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess" }""";

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        Assert.Null(model.ReadmeSummary);
    }

    [Fact]
    public async Task BuildAsync_ReportsOrphanAndUnresolvedInvokeRisks() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.XamlFiles.Add($"{Root}/Main.xaml");
        fs.XamlFiles.Add($"{Root}/Orphan.xaml");
        fs.FileContents[$"{Root}/Main.xaml"] = MinimalXaml; // invokes Sub.xaml, which does not exist
        fs.FileContents[$"{Root}/Orphan.xaml"] = MinimalXaml;

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        Assert.Contains(model.Risks, r => r.StartsWith("Orphan workflow (not invoked from Main): Orphan.xaml"));
        Assert.Contains(model.Risks, r => r.Contains("Unresolved workflow invocation") && r.Contains("Sub.xaml"));
    }

    [Fact]
    public async Task BuildAsync_MalformedXaml_AddsRiskWithoutThrowing() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.XamlFiles.Add($"{Root}/Main.xaml");
        fs.FileContents[$"{Root}/Main.xaml"] = "<Activity><broken></Activity>";

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        var workflow = Assert.Single(model.Workflows);
        Assert.True(workflow.HasParseError);
        Assert.Contains(model.Risks, r => r.Contains("XAML parse failure"));
    }

    [Fact]
    public async Task BuildAsync_ParsesCodedFilesIntoModel() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.XamlFiles.Add($"{Root}/Main.xaml");
        fs.FileContents[$"{Root}/Main.xaml"] = MinimalXaml;
        fs.CSharpFiles.Add($"{Root}/InvoiceFlow.cs");
        fs.FileContents[$"{Root}/InvoiceFlow.cs"] = """
            namespace testProcess
            {
                public class InvoiceFlow : CodedWorkflow
                {
                    [Workflow]
                    public void Execute() { }
                }
            }
            """;

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        var coded = Assert.Single(model.CodedWorkflows);
        Assert.Equal("InvoiceFlow.cs", coded.FileName);
        Assert.True(coded.IsCodedWorkflow);
        Assert.Equal(CodedFileKind.Workflow, coded.Kind);
        Assert.Equal(["Execute"], coded.EntryMethods);
        Assert.DoesNotContain(model.Risks, r => r.Contains("InvoiceFlow.cs"));
    }

    [Fact]
    public async Task BuildAsync_UnreadableCodedFile_AddsRiskWithoutThrowing() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.CSharpFiles.Add($"{Root}/Gone.cs"); // no FileContents entry -> ReadAllText throws

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        var coded = Assert.Single(model.CodedWorkflows);
        Assert.True(coded.HasParseError);
        Assert.Contains(model.Risks, r => r.Contains("Gone.cs") && r.Contains("C# parse failure"));
    }

    [Fact]
    public async Task BuildAsync_PopulatesFolderStructureFromFilesystem() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = Json };
        fs.FileContents[Json] = """{ "name": "testProcess", "main": "Main.xaml" }""";
        fs.DirectoryTree = new UiPath.Engineering.Mcp.Core.Models.DirectoryTreeNode {
            Name = "testProcess",
            Path = Root,
            IsDirectory = true,
            Children =
            [
                new UiPath.Engineering.Mcp.Core.Models.DirectoryTreeNode { Name = "Main.xaml", Path = $"{Root}/Main.xaml" }
            ]
        };

        var model = await new ProjectModelBuilder(fs).BuildAsync(Root);

        Assert.NotNull(model.FolderStructure);
        Assert.Equal("testProcess", model.FolderStructure.Name);
        Assert.True(model.FolderStructure.IsDirectory);
        Assert.Equal("Main.xaml", Assert.Single(model.FolderStructure.Children).Name);
    }
}
