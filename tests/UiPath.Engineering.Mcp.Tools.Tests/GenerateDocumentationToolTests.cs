using System.Text.Json;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Canvas;
using UiPath.Engineering.Mcp.Core.Configuration;
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
    public async Task GenerateDocumentation_IncludesActivitiesNestedTwoOrMoreDeep() {
        // The parser links parents to children; the outline must follow Children, not Depth,
        // or anything below the second level stays invisible in generated docs.
        var deepest = new ActivityModel { Id = "sequence.1/if.1/then.1", ParentId = "sequence.1/if.1", DisplayName = "Log deep", Type = "LogMessage", Depth = 2 };
        var ifNode = new ActivityModel {
            Id = "sequence.1/if.1",
            ParentId = "sequence.1",
            DisplayName = "Decide",
            Type = "If",
            Depth = 1,
            Children = [deepest]
        };
        var root = new ActivityModel { Id = "sequence.1", DisplayName = "Main", Type = "Sequence", Depth = 0, Children = [ifNode] };

        var model = new UiPathProjectModel {
            ProjectName = "testProcess",
            ProjectPath = "/projects/testProcess",
            MainWorkflow = "Main.xaml",
            Workflows = [
                new WorkflowModel {
                    FileName = "Main.xaml",
                    IsMain = true,
                    Activities = [root, ifNode, deepest]
                }
            ]
        };

        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Contains("Log deep", FindActivityOutline(result.Data));
    }

    private static string FindActivityOutline(object? data) {
        var json = System.Text.Json.JsonSerializer.Serialize(data);
        var marker = "\"ActivityOutline\":";
        var at = json.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? string.Empty : json[at..];
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
    public async Task GenerateDocumentation_WhenProjectJsonMissing_PropagatesFileNotFound() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new FileNotFoundException("project.json not found.") };
        var tool = new GenerateDocumentationTool(fs, builder);

        await Assert.ThrowsAsync<FileNotFoundException>(() => tool.GenerateDocumentation("/projects/empty"));
    }

    [Fact]
    public async Task GenerateDocumentation_WhenUnexpectedError_PropagatesToHostExceptionBoundary() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new InvalidOperationException("boom") };
        var tool = new GenerateDocumentationTool(fs, builder);

        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.GenerateDocumentation("/projects/testProcess"));
    }

    [Fact]
    public async Task GenerateDocumentation_OmittingFormat_WritesNoSnapshot() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.GenerateDocumentation("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Documentation data generated for project 'testProcess' (2 workflows, 1 risks).", result.Summary);
        Assert.Empty(fs.Writes);
        var method = typeof(GenerateDocumentationTool).GetMethod(nameof(GenerateDocumentationTool.GenerateDocumentation));
        var attr = method!.GetCustomAttributes(false).OfType<ModelContextProtocol.Server.McpServerToolAttribute>().Single();
        Assert.False(attr.ReadOnly);
    }

    [Fact]
    public async Task GenerateDocumentation_CanvasSnapshot_WritesSkeletonAndShortSummary() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        fs.FileContents["/projects/testProcess/Main.xaml"] = "main-bytes";
        fs.FileContents["/projects/testProcess/Sub.xaml"] = "sub-bytes";
        var model = BuildModel();
        model.Workflows[0].FilePath = "/projects/testProcess/Main.xaml";
        model.Workflows[0].RelativePath = "Main.xaml";
        model.Workflows[1].FilePath = "/projects/testProcess/Sub.xaml";
        model.Workflows[1].RelativePath = "Sub.xaml";
        model.Workflows[0].InvokeWorkflows[0].ArgumentMappings.Add(new ArgumentMappingModel {
            Direction = "In",
            TargetArgument = "in_Config",
            Expression = "password=hunter2"
        });
        var tool = new GenerateDocumentationTool(
            fs,
            new FakeProjectModelBuilder { Model = model },
            Microsoft.Extensions.Options.Options.Create(new McpServerOptions { Version = "9.9.9" }));

        var result = await tool.GenerateDocumentation("/projects/testProcess", "canvasSnapshot");

        Assert.Equal("success", result.Status);
        var path = CanvasSnapshotPath.ForProject("/projects/testProcess");
        Assert.Equal(path, fs.Writes.Keys.Single());
        Assert.Contains(Path.GetDirectoryName(path)!, fs.CreatedDirectories);
        var payload = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.DoesNotContain("schemaVersion", payload);
        Assert.Contains("\"nodeCount\":2", payload);
        Assert.Contains("\"edgeCount\":2", payload);
        using var doc = JsonDocument.Parse(fs.Writes[path]);
        var generatedAt = doc.RootElement.GetProperty("generatedAt").GetString();
        Assert.Contains(generatedAt!, result.Summary);
        Assert.Contains(path, result.Summary);
        Assert.Equal("9.9.9", doc.RootElement.GetProperty("generator").GetProperty("mcpVersion").GetString());
        Assert.Equal("", doc.RootElement.GetProperty("nodes")[0].GetProperty("explanation").GetString());
        Assert.Contains("password=***REDACTED***", fs.Writes[path]);
        Assert.DoesNotContain("/projects/testProcess", doc.RootElement.GetRawText());
    }

    [Fact]
    public async Task GenerateDocumentation_CanvasSnapshot_PathNotAllowed_WritesNothing() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder());

        var result = await tool.GenerateDocumentation("/not/allowed", "canvasSnapshot");

        Assert.Equal("error", result.Status);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task GenerateDocumentation_UnknownFormat_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new GenerateDocumentationTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.GenerateDocumentation("/projects/testProcess", "markdown");

        Assert.Equal("error", result.Status);
        Assert.Contains("format must be omitted or canvasSnapshot.", result.Summary);
        Assert.Empty(fs.Writes);
    }
}
