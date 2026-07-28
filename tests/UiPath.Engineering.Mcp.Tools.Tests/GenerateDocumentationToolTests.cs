using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GenerateDocumentationToolTests {
    private static UiPathProjectModel BuildModel() => new() {
        ProjectName = "testProcess",
        ProjectPath = "/projects/testProcess",
        MainWorkflow = "Main.xaml",
        Description = "A test process.",
        ReadmeSummary = "Does test things.",
        Packages = [new PackageModel { Id = "UiPath.System.Activities", Version = "24.10.0" }],
        Risks = ["Cycle detected: Main.xaml -> Sub.xaml -> Main.xaml"],
        Workflows =
        [
            new WorkflowModel {
                FileName = "Main.xaml",
                IsMain = true,
                Arguments = [new ArgumentModel { Name = "in_Config", Direction = "In", Type = "Dictionary" }],
                Variables = [new VariableModel { Name = "counter", Type = "Int32" }],
                Activities = [new ActivityModel { DisplayName = "Main", Type = "Sequence", Depth = 0 }],
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Sub.xaml" }],
                LogMessages = [new LogMessageModel { DisplayName = "Log", Level = "Info", Message = "hi" }]
            },
            new WorkflowModel {
                FileName = "Sub.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Sub.xaml", TargetWorkflow = "Main.xaml" }]
            }
        ]
    };

    [Fact]
    public async Task GenerateDocumentation_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder());

        var result = await tool.GenerateDocumentation("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task GenerateDocumentation_HappyPath_ReturnsStructuredData() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Documentation data generated for project 'testProcess' (2 workflows, 1 risks).", result.Summary);
        Assert.NotNull(result.Data);

        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("testProcess", json);
        Assert.Contains("UiPath.System.Activities", json);
        Assert.Contains("Cycles", json);
        Assert.Contains("Orphans", json);
        Assert.Contains("Cycle detected", json);
    }

    [Fact]
    public async Task GenerateDocumentation_IncludesDependencyGraphEdgesAndCycles() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("success", result.Status);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        // Both directions of the Main <-> Sub cycle must appear as resolved edges.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(json, "\"IsResolved\":true").Count);
        Assert.Contains("Main.xaml", json);
        Assert.Contains("Sub.xaml", json);
    }

    [Fact]
    public async Task GenerateDocumentation_WhenWorkflowHasParseError_IncludedInOutput() {
        var model = BuildModel();
        model.Workflows[1].HasParseError = true;
        model.Workflows[1].ParseError = "Invalid XML at line 5.";
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("success", result.Status);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("Invalid XML at line 5.", json);
    }

    [Fact]
    public async Task GenerateDocumentation_WhenProjectJsonMissing_ReturnsStructuredError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new FileNotFoundException("project.json not found.") };
        var tool = new GenerateDocumentationTool(fs, builder);

        var result = await tool.GenerateDocumentation("/projects/empty");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Fact]
    public async Task GenerateDocumentation_WhenUnexpectedError_DoesNotThrowAndReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new InvalidOperationException("boom") };
        var tool = new GenerateDocumentationTool(fs, builder);

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Equal("Documentation generation failed.", result.Summary);
        Assert.Contains("boom", result.Errors);
    }
}
