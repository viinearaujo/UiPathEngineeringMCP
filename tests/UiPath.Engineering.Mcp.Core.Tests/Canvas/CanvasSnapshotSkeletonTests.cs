using System.Text;
using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Canvas;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests.Canvas;

public class CanvasSnapshotSkeletonTests {
    private const string ProjectPath = "/projects/testProcess";
    private static readonly DateTime Stamp = new(2026, 9, 26, 16, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_WritesXamlAndCodedWorkflowNodes_OmitsSourceAndTest() {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) {
            ["/projects/testProcess/Main.xaml"] = Encoding.UTF8.GetBytes("main-bytes"),
            ["/projects/testProcess/Broken.xaml"] = Encoding.UTF8.GetBytes("broken-bytes"),
            ["/projects/testProcess/Coded/Process.cs"] = Encoding.UTF8.GetBytes("coded-bytes")
        };
        var model = new UiPathProjectModel {
            ProjectName = "Dispatch",
            ProjectPath = ProjectPath,
            MainWorkflow = "Main.xaml",
            Workflows = [
                new WorkflowModel {
                    FileName = "Main.xaml",
                    FilePath = "/projects/testProcess/Main.xaml",
                    RelativePath = "Main.xaml",
                    Arguments = [new ArgumentModel { Name = "in_Config", Direction = "In", Type = "Dictionary<String,Object>" }],
                    ExceptionHandlers = [new ExceptionHandlerModel { HasGlobalHandler = true }]
                },
                new WorkflowModel {
                    FileName = "Broken.xaml",
                    FilePath = "/projects/testProcess/Broken.xaml",
                    RelativePath = "Broken.xaml",
                    HasParseError = true,
                    ParseError = "Invalid XML at line 5.",
                    ExceptionHandlers = [new ExceptionHandlerModel { HasGlobalHandler = false }]
                },
                new WorkflowModel {
                    FileName = "Process.cs",
                    FilePath = "/projects/testProcess/Coded/Process.cs",
                    RelativePath = "Coded/Process.cs"
                }
            ],
            CodedWorkflows = [
                new CodedWorkflowModel {
                    FileName = "Process.cs",
                    FilePath = "/projects/testProcess/Coded/Process.cs",
                    Kind = CodedFileKind.Workflow,
                    EntryHasTryCatch = true,
                    EntryArguments = [new ArgumentModel { Name = "folder", Direction = "In", Type = "String" }]
                },
                new CodedWorkflowModel {
                    FileName = "Helper.cs",
                    FilePath = "/projects/testProcess/Helper.cs",
                    Kind = CodedFileKind.Source
                },
                new CodedWorkflowModel {
                    FileName = "Tests.cs",
                    FilePath = "/projects/testProcess/Tests.cs",
                    Kind = CodedFileKind.Test,
                    EntryHasTryCatch = true
                }
            ]
        };

        var draft = CanvasSnapshotSkeleton.Build(model, ProjectPath, "9.9.9", path => files[path], Stamp);

        Assert.Null(draft.Error);
        Assert.Equal("2026-09-26T16:00:00Z", draft.GeneratedAt);
        Assert.Equal(3, draft.NodeCount);
        Assert.DoesNotContain(ProjectPath, draft.Json);
        Assert.DoesNotContain("\"activities\"", draft.Json);
        Assert.DoesNotContain("\"cycles\"", draft.Json);
        Assert.DoesNotContain("\"orphans\"", draft.Json);
        Assert.DoesNotContain("Helper.cs", draft.Json);
        Assert.DoesNotContain("Tests.cs", draft.Json);

        using var doc = JsonDocument.Parse(draft.Json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-09-26T16:00:00Z", root.GetProperty("generatedAt").GetString());
        Assert.Equal("9.9.9", root.GetProperty("generator").GetProperty("mcpVersion").GetString());
        Assert.Equal("Dispatch", root.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal("Main.xaml", root.GetProperty("project").GetProperty("entryPoint").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("project").GetProperty("additionalEntryPoints").ValueKind);
        Assert.Equal("", root.GetProperty("project").GetProperty("overview").GetString());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Equal(["Main.xaml", "Broken.xaml", "Coded/Process.cs"], nodes.Select(n => n.GetProperty("id").GetString()).ToArray());
        Assert.Equal("xaml", nodes[0].GetProperty("kind").GetString());
        Assert.Equal("17ab370c8c4acebf3ac18de004d3d50e5b40e4b6b415193f90a77a7174d6ce25", nodes[0].GetProperty("sha256").GetString());
        Assert.True(nodes[0].GetProperty("hasExceptionHandler").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nodes[0].GetProperty("parseError").ValueKind);
        Assert.Equal("", nodes[0].GetProperty("explanation").GetString());
        Assert.Empty(nodes[0].GetProperty("decisions").EnumerateArray());
        var argument = nodes[0].GetProperty("arguments").EnumerateArray().Single();
        Assert.Equal("in_Config", argument.GetProperty("name").GetString());
        Assert.Equal("In", argument.GetProperty("direction").GetString());
        Assert.Equal("Dictionary<String,Object>", argument.GetProperty("type").GetString());

        Assert.False(nodes[1].GetProperty("hasExceptionHandler").GetBoolean());
        Assert.Equal("Invalid XML at line 5.", nodes[1].GetProperty("parseError").GetString());

        Assert.Equal("coded", nodes[2].GetProperty("kind").GetString());
        Assert.Equal("ec5583cd13079244a2e1bd627fd6967d4ce3f9f33325b489712d7a41e4b14ff1", nodes[2].GetProperty("sha256").GetString());
        Assert.True(nodes[2].GetProperty("hasExceptionHandler").GetBoolean());
        Assert.Equal("folder", nodes[2].GetProperty("arguments")[0].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("edges").ValueKind);
        Assert.Equal(0, root.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public void Build_EntryPointNull_WhenMainMissingOrBlank() {
        var model = new UiPathProjectModel {
            ProjectName = "Dispatch",
            MainWorkflow = "   ",
            Workflows = [
                new WorkflowModel {
                    FileName = "Main.xaml",
                    FilePath = "/projects/testProcess/Main.xaml",
                    RelativePath = "Main.xaml"
                }
            ]
        };

        var draft = CanvasSnapshotSkeleton.Build(
            model,
            ProjectPath,
            "0.1.0",
            _ => Encoding.UTF8.GetBytes("main-bytes"),
            Stamp);

        using var doc = JsonDocument.Parse(draft.Json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("project").GetProperty("entryPoint").ValueKind);
    }
}
