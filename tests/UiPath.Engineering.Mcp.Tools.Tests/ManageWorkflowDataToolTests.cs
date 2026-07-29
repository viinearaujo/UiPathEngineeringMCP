using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ManageWorkflowDataToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <Sequence.Variables>
              <Variable x:TypeArguments="x:String" Name="message" />
            </Sequence.Variables>
            <WriteLine DisplayName="Say" Text="[message]" />
          </Sequence>
        </Activity>
        """;

    private static (FakeFilesystemProvider Fs, ManageWorkflowDataTool Tool, string Target) CreateTool() {
        var fs = new FakeFilesystemProvider();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = Workflow;
        return (fs, new ManageWorkflowDataTool(fs), target);
    }

    [Fact]
    public void ManageWorkflowData_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new ManageWorkflowDataTool(fs);

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "add", "variable", "count", type: "Int32");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ManageWorkflowData_RejectsNonXamlFile() {
        var (_, tool, _) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.cs", "add", "variable", "count", type: "Int32");

        Assert.Equal("error", result.Status);
        Assert.Contains(".xaml", result.Summary);
    }

    [Fact]
    public void ManageWorkflowData_RejectsUnknownOperation() {
        var (_, tool, _) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "mutate", "variable", "count", type: "Int32");

        Assert.Equal("error", result.Status);
        Assert.Contains("add, remove, or rename", result.Summary);
    }

    [Fact]
    public void ManageWorkflowData_RejectsUnknownKind() {
        var (_, tool, _) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "add", "parameter", "count", type: "Int32");

        Assert.Equal("error", result.Status);
        Assert.Contains("variable or argument", result.Summary);
    }

    [Fact]
    public void ManageWorkflowData_RejectsPathOutsideProject() {
        var (_, tool, _) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "../Main.xaml", "remove", "variable", "message");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void ManageWorkflowData_WhenFileMissing_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new ManageWorkflowDataTool(fs);

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "remove", "variable", "message");

        Assert.Equal("error", result.Status);
        Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManageWorkflowData_AddVariable_WritesUpdatedFile() {
        var (fs, tool, target) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "add", "variable", "count", type: "Int32");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("add", data.GetProperty("operation").GetString());
        Assert.Equal("variable", data.GetProperty("kind").GetString());
        var written = fs.Writes[target];
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"count\"", written);
        // Existing content is untouched.
        Assert.Contains("<Variable x:TypeArguments=\"x:String\" Name=\"message\"", written);
    }

    [Fact]
    public void ManageWorkflowData_AddOutArgument_WritesOutArgumentProperty() {
        var (fs, tool, target) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "add", "argument", "result",
            type: "String", direction: "Out");

        Assert.Equal("success", result.Status);
        Assert.Contains("<x:Property Name=\"result\" Type=\"OutArgument(x:String)\" />", fs.Writes[target]);
    }

    [Fact]
    public void ManageWorkflowData_AddDuplicate_MapsDeclarationConflictError() {
        var (fs, tool, target) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "add", "variable", "message", type: "String");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.DataDeclarationConflict);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public void ManageWorkflowData_RemoveMissing_MapsDeclarationNotFoundError() {
        var (fs, tool, target) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "remove", "variable", "missing");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails!, e => e.ErrorCode == ToolErrorCodes.DataDeclarationNotFound);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public void ManageWorkflowData_RenameVariable_ReturnsWarningAndWrites() {
        var (fs, tool, target) = CreateTool();

        var result = tool.ManageWorkflowData(ProjectPath, "Main.xaml", "rename", "variable",
            "message", newName: "greeting");

        Assert.Equal("success", result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("message") && w.Contains("greeting"));
        Assert.Contains("Name=\"greeting\"", fs.Writes[target]);
    }
}
