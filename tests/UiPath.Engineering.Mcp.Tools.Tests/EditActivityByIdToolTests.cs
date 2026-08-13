using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class EditActivityByIdToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence DisplayName="Main">
            <ui:LogMessage DisplayName="Start" Message="begin" />
          </Sequence>
        </Activity>
        """;

    private const string AssignSpec = """
        {
          "name": "Sequence",
          "children": [
            { "name": "Assign", "properties": { "DisplayName": "Set total", "To": "[total]", "Value": "[42]" } }
          ]
        }
        """;

    private static string Target(string relative) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relative.Replace('/', Path.DirectorySeparatorChar));

    private static (FakeFilesystemProvider Fs, string Path) FilesystemWithWorkflow() {
        var fs = new FakeFilesystemProvider();
        var target = Target("Main.xaml");
        fs.FileContents[target] = Workflow;
        return (fs, target);
    }

    [Fact]
    public void EditWorkflowActivity_ById_ReplacesAndReportsId() {
        var (fs, target) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "replace",
            activityId: "sequence.1/logmessage.1",
            fragment: "<ui:Comment DisplayName=\"Note\" />");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal("sequence.1/logmessage.1", data.GetProperty("activityId").GetString());
        Assert.Contains("<ui:Comment DisplayName=\"Note\"", fs.Writes[target]);
        Assert.Contains(result.Warnings, w => w.Contains("find_activity"));
    }

    [Fact]
    public void EditWorkflowActivity_ByIdWithMismatchedType_ReturnsStaleError() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove",
            activityId: "sequence.1", activityType: "LogMessage");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.ActivityIdStale);
        Assert.Equal("find_activity", error.SuggestedTool);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowActivity_ByIdWithMatchingDisplayName_Succeeds() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove",
            activityId: "sequence.1/logmessage.1", displayName: "Start");

        Assert.Equal("success", result.Status);
        Assert.DoesNotContain("DisplayName=\"Start\"", fs.Writes[Target("Main.xaml")]);
    }

    [Fact]
    public void EditWorkflowActivity_NeitherIdNorDisplayName_ReturnsInvalidArgument() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public void EditWorkflowActivity_AmbiguousDisplayName_ReturnsStructuredCode() {
        var fs = new FakeFilesystemProvider();
        fs.FileContents[Target("Main.xaml")] = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main">
                <ui:LogMessage DisplayName="Dup" Message="a" />
                <ui:LogMessage DisplayName="Dup" Message="b" />
              </Sequence>
            </Activity>
            """;
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove", displayName: "Dup");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.AmbiguousActivity);
    }

    [Fact]
    public void InsertActivities_ById_InsertsIntoResolvedContainer() {
        var (fs, target) = FilesystemWithWorkflow();
        var tool = new InsertActivitiesTool(fs);

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec,
            activityId: "sequence.1");

        Assert.Equal("success", result.Status);
        Assert.Contains("DisplayName=\"Set total\"", fs.Writes[target]);
    }

    [Fact]
    public void InsertActivities_NeitherIdNorDisplayName_ReturnsInvalidArgument() {
        var (fs, _) = FilesystemWithWorkflow();
        var tool = new InsertActivitiesTool(fs);

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.InvalidArgument);
    }
}
