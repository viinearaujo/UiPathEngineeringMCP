using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ExplainWorkflowToolTests {
    private static UiPathProjectModel BuildModel() => new() {
        ProjectName = "testProcess",
        MainWorkflow = "Main.xaml",
        Workflows =
        [
            new WorkflowModel {
                FileName = "Main.xaml",
                FilePath = "/projects/testProcess/Main.xaml",
                IsMain = true,
                Arguments = [new ArgumentModel { Name = "in_Config", Direction = "In", Type = "Dictionary" }],
                Variables = [new VariableModel { Name = "counter", Type = "Int32", Scope = "Sequence" }],
                Activities =
                [
                    new ActivityModel { DisplayName = "Main", Type = "Sequence", Depth = 0 },
                    new ActivityModel { DisplayName = "Log start", Type = "LogMessage", Depth = 1 }
                ],
                ExceptionHandlers = [new ExceptionHandlerModel { WorkflowName = "Main.xaml", HasGlobalHandler = true }],
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Sub.xaml", DisplayName = "Invoke Sub" }],
                LogMessages = [new LogMessageModel { DisplayName = "Log start", Level = "Info", Message = "Started" }]
            },
            new WorkflowModel { FileName = "Sub.xaml", FilePath = "/projects/testProcess/Sub.xaml" }
        ]
    };

    [Fact]
    public async Task ExplainWorkflow_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder());

        var result = await tool.ExplainWorkflow("/not/allowed", "Main.xaml");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ExplainWorkflow_HappyPath_ReturnsWorkflowDetails() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml");

        Assert.Equal("success", result.Status);
        Assert.Equal("Workflow 'Main.xaml': 1 arguments, 1 variables, 2 activities, 1 exception handlers, invokes 1 workflows.", result.Summary);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("MAIN.XAML")]
    [InlineData("subfolder/Main.xaml")]
    public async Task ExplainWorkflow_MatchesCaseInsensitiveWithoutExtensionOrPath(string workflowFile) {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.ExplainWorkflow("/projects/testProcess", workflowFile);

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task ExplainWorkflow_WhenWorkflowNotFound_ReturnsErrorWithAvailableWorkflows() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Missing.xaml");

        Assert.Equal("error", result.Status);
        Assert.Equal("Workflow 'Missing.xaml' not found.", result.Summary);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("Main.xaml", json);
        Assert.Contains("Sub.xaml", json);
    }

    [Fact]
    public async Task ExplainWorkflow_WhenWorkflowHasParseError_SurfacesWarningGracefully() {
        var model = BuildModel();
        model.Workflows.Add(new WorkflowModel {
            FileName = "Broken.xaml",
            HasParseError = true,
            ParseError = "Invalid XML at line 5."
        });
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Broken.xaml");

        Assert.Equal("success", result.Status);
        Assert.Single(result.Warnings);
        Assert.Contains("Invalid XML at line 5.", result.Warnings[0]);
    }

    [Theory]
    [InlineData("InvoiceFlow")]
    [InlineData("InvoiceFlow.cs")]
    [InlineData("subfolder/InvoiceFlow.cs")]
    public async Task ExplainWorkflow_CodedWorkflow_MatchedWithOrWithoutSuffixOrPath(string workflowFile) {
        var model = BuildModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "InvoiceFlow.cs",
            FilePath = "/projects/testProcess/InvoiceFlow.cs",
            ClassName = "InvoiceFlow",
            Namespace = "testProcess",
            Kind = CodedFileKind.Workflow,
            IsCodedWorkflow = true,
            EntryMethods = ["Execute"],
            PublicMethods = ["CalculateTotal"]
        });
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.ExplainWorkflow("/projects/testProcess", workflowFile);

        Assert.Equal("success", result.Status);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"className\":\"InvoiceFlow\"", json);
        Assert.Contains("\"kind\":\"workflow\"", json);
        Assert.Contains("\"isCodedWorkflow\":true", json);
        Assert.Contains("Execute", json);
        Assert.Contains("CalculateTotal", json);
    }

    [Fact]
    public async Task ExplainWorkflow_CodedWorkflowWithParseError_SurfacesWarningGracefully() {
        var model = BuildModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "Broken.cs",
            HasParseError = true,
            ParseError = "C# parse failure: no class declaration found."
        });
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Broken.cs");

        Assert.Equal("success", result.Status);
        Assert.Single(result.Warnings);
        Assert.Contains("no class declaration found.", result.Warnings[0]);
    }

    [Fact]
    public async Task ExplainWorkflow_NotFound_ListsBothXamlAndCodedFiles() {
        var model = BuildModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel { FileName = "Helpers.cs" });
        var fs = new FakeFilesystemProvider { Allowed = true };
        var tool = new ExplainWorkflowTool(fs, new FakeProjectModelBuilder { Model = model });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Missing.xaml");

        Assert.Equal("error", result.Status);
        Assert.Equal("Workflow 'Missing.xaml' not found.", result.Summary);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("Main.xaml", json);
        Assert.Contains("Helpers.cs", json);
    }

    [Fact]
    public async Task ExplainWorkflow_WhenProjectJsonMissing_ReturnsStructuredError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new FileNotFoundException("project.json not found.") };
        var tool = new ExplainWorkflowTool(fs, builder);

        var result = await tool.ExplainWorkflow("/projects/empty", "Main.xaml");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Fact]
    public async Task ExplainWorkflow_WhenUnexpectedError_DoesNotThrowAndReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var builder = new FakeProjectModelBuilder { ToThrow = new InvalidOperationException("boom") };
        var tool = new ExplainWorkflowTool(fs, builder);

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml");

        Assert.Equal("error", result.Status);
        Assert.Equal("Workflow explanation failed.", result.Summary);
        Assert.Contains("boom", result.Errors);
    }

    [Fact]
    public async Task ExplainWorkflow_WithActivityTree_NestsChildren() {
        // Build the model through the real parser so IDs/Children are wired.
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main">
                <If DisplayName="Check">
                  <If.Then>
                    <ui:LogMessage DisplayName="Log yes" Message="y" />
                  </If.Then>
                </If>
              </Sequence>
            </Activity>
            """;
        var workflow = new Core.Parsing.XamlWorkflowParser().Parse("Main.xaml", "/proj/Main.xaml", xaml);
        var builder = new FakeProjectModelBuilder {
            Model = new UiPathProjectModel {
                ProjectName = "testProcess",
                MainWorkflow = "Main.xaml",
                Workflows = [workflow]
            }
        };
        var tool = new ExplainWorkflowTool(new FakeFilesystemProvider(), builder);

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml", includeActivityTree: true);

        Assert.Equal("success", result.Status);
        var tree = JsonSerializer.SerializeToElement(result.Data).GetProperty("activityTree");
        var root = tree[0];
        Assert.Equal("sequence.1", root.GetProperty("id").GetString());
        var ifNode = root.GetProperty("children")[0];
        Assert.Equal("sequence.1/if.1", ifNode.GetProperty("id").GetString());
        var logNode = ifNode.GetProperty("children")[0];
        Assert.Equal("sequence.1/if.1/logmessage.1", logNode.GetProperty("id").GetString());
        Assert.True(logNode.GetProperty("line").GetInt32() > 0);
    }

    [Fact]
    public async Task ExplainWorkflow_WithoutFlag_ActivityTreeIsNull() {
        var tool = new ExplainWorkflowTool(new FakeFilesystemProvider { Allowed = true },
            new FakeProjectModelBuilder { Model = BuildModel() });

        var result = await tool.ExplainWorkflow("/projects/testProcess", "Main.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("activityTree").ValueKind);
    }
}
