using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetWorkflowDependenciesToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static UiPathProjectModel SampleModel() => new() {
        ProjectName = "testProcess",
        MainWorkflow = "Main.xaml",
        Workflows = [
            new WorkflowModel {
                FileName = "Main.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "Main.xaml",
                    TargetWorkflow = "Child.xaml",
                    DisplayName = "Invoke child",
                    ArgumentMappings = [new ArgumentMappingModel {
                        Direction = "In", TargetArgument = "in_CustomerId", Expression = "[customerId]"
                    }]
                }]
            },
            new WorkflowModel { FileName = "Child.xaml" },
            new WorkflowModel { FileName = "Orphan.xaml" }
        ]
    };

    private static GetWorkflowDependenciesTool Tool(UiPathProjectModel model) =>
        new(new FakeFilesystemProvider(), new FakeProjectModelBuilder { Model = model });

    [Fact]
    public async Task PerWorkflow_ReturnsCallersAndCalleesWithMappings() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Main.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var callees = data.GetProperty("callees");
        Assert.Equal("Child.xaml", callees[0].GetProperty("targetWorkflow").GetString());
        var mapping = callees[0].GetProperty("argumentMappings")[0];
        Assert.Equal("in_CustomerId", mapping.GetProperty("targetArgument").GetString());
        Assert.Equal("[customerId]", mapping.GetProperty("expression").GetString());
        Assert.Equal(0, data.GetProperty("callers").GetArrayLength());
    }

    [Fact]
    public async Task PerWorkflow_ChildSeesItsCaller() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Child.xaml");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var callers = data.GetProperty("callers");
        Assert.Equal(1, callers.GetArrayLength());
        Assert.Equal("Main.xaml", callers[0].GetProperty("sourceWorkflow").GetString());
    }

    [Fact]
    public async Task ProjectWide_ReturnsEdgesCyclesOrphansUnresolved() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(1, data.GetProperty("edges").GetArrayLength());
        Assert.Equal(0, data.GetProperty("cycles").GetArrayLength());
        Assert.Contains(data.GetProperty("orphans").EnumerateArray(),
            o => o.GetString() == "Orphan.xaml");
        Assert.Equal(0, data.GetProperty("unresolved").GetArrayLength());
    }

    [Fact]
    public async Task UnknownWorkflow_ReturnsErrorListingAvailable() {
        var tool = Tool(SampleModel());

        var result = await tool.GetWorkflowDependencies(ProjectPath, "Missing.xaml");

        Assert.Equal("error", result.Status);
        Assert.Contains("Missing.xaml", result.Summary);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Contains(data.GetProperty("availableWorkflows").EnumerateArray(),
            w => w.GetString() == "Main.xaml");
    }
}
