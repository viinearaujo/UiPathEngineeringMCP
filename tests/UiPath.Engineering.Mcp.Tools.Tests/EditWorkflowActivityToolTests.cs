using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class XamlActivityEditorTests {
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

    [Fact]
    public void Insert_AppendsFragmentInsideTargetSequence() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Insert, "Main",
            fragment: "<ui:LogMessage DisplayName=\"End\" Message=\"done\" />");

        Assert.True(result.Success, result.Error);
        var startIndex = result.UpdatedContent!.IndexOf("DisplayName=\"Start\"", StringComparison.Ordinal);
        var endIndex = result.UpdatedContent.IndexOf("DisplayName=\"End\"", StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        // The ui: namespace is reused, not re-declared with a synthetic prefix.
        Assert.Contains("<ui:LogMessage DisplayName=\"End\"", result.UpdatedContent);
    }

    [Fact]
    public void Insert_WithPositionFirst_PrependsFragment() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Insert, "Main",
            fragment: "<ui:LogMessage DisplayName=\"End\" Message=\"done\" />",
            position: XamlActivityEditor.First);

        Assert.True(result.Success, result.Error);
        var startIndex = result.UpdatedContent!.IndexOf("DisplayName=\"Start\"", StringComparison.Ordinal);
        var endIndex = result.UpdatedContent.IndexOf("DisplayName=\"End\"", StringComparison.Ordinal);
        Assert.True(endIndex >= 0 && endIndex < startIndex);
    }

    [Fact]
    public void Insert_UnprefixedFragment_StaysInDefaultNamespace() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Insert, "Main",
            fragment: "<WriteLine DisplayName=\"Say\" Text=\"hi\" />");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<WriteLine DisplayName=\"Say\"", result.UpdatedContent);
        var updated = result.UpdatedContent!;
        Assert.DoesNotContain("xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"",
            updated[updated.IndexOf("<WriteLine", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Replace_SwapsActivityKeepingSurroundings() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Replace, "Start",
            fragment: "<ui:Comment DisplayName=\"Note\" Text=\"rework\" />");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("DisplayName=\"Start\"", result.UpdatedContent);
        Assert.Contains("<ui:Comment DisplayName=\"Note\"", result.UpdatedContent);
        Assert.Contains("DisplayName=\"Main\"", result.UpdatedContent);
    }

    [Fact]
    public void Remove_DropsActivityWithoutBlankLine() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Start");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("DisplayName=\"Start\"", result.UpdatedContent);
        Assert.Contains("DisplayName=\"Main\"", result.UpdatedContent);
    }

    [Fact]
    public void Insert_WhenTargetLacksUiPrefix_DeclaresItOnFragment() {
        // Real UiPath files often declare namespaces only at point of use, not at the root.
        const string noUiRoot = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main" />
            </Activity>
            """;

        var result = XamlActivityEditor.Edit(noUiRoot, XamlActivityEditor.Insert, "Main",
            fragment: "<ui:LogMessage DisplayName=\"End\" Message=\"done\" />");

        Assert.True(result.Success, result.Error);
        Assert.Contains(
            "<ui:LogMessage DisplayName=\"End\" Message=\"done\" xmlns:ui=\"http://schemas.uipath.com/workflow/activities\" />",
            result.UpdatedContent);
        Assert.DoesNotContain("xmlns=\"http://schemas.uipath.com/workflow/activities\"", result.UpdatedContent);
    }

    [Fact]
    public void Edit_WhenDisplayNameNotFound_ReturnsError() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Missing");

        Assert.False(result.Success);
        Assert.Contains("No activity found", result.Error);
    }

    [Fact]
    public void Edit_WhenMultipleMatches_ReturnsError() {
        var duplicated = Workflow.Replace(
            "<ui:LogMessage DisplayName=\"Start\" Message=\"begin\" />",
            "<ui:LogMessage DisplayName=\"Start\" Message=\"a\" />\n    <ui:LogMessage DisplayName=\"Start\" Message=\"b\" />");

        var result = XamlActivityEditor.Edit(duplicated, XamlActivityEditor.Remove, "Start");

        Assert.False(result.Success);
        Assert.Contains("activityType", result.Error);
    }

    [Fact]
    public void Edit_ActivityTypeNarrowsMatches() {
        const string ambiguous = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Step">
                <ui:LogMessage DisplayName="Step" Message="keep" />
              </Sequence>
            </Activity>
            """;

        var result = XamlActivityEditor.Edit(ambiguous, XamlActivityEditor.Remove, "Step",
            activityType: "LogMessage");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Sequence DisplayName=\"Step\">", result.UpdatedContent);
        Assert.DoesNotContain("LogMessage", result.UpdatedContent);
    }

    [Fact]
    public void Edit_WhenFragmentInvalid_ReturnsError() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Insert, "Main",
            fragment: "<ui:LogMessage");

        Assert.False(result.Success);
        Assert.Contains("not valid XAML", result.Error);
    }

    [Fact]
    public void Edit_WhenWorkflowMalformed_ReturnsError() {
        var result = XamlActivityEditor.Edit("<Activity", XamlActivityEditor.Remove, "Start");

        Assert.False(result.Success);
        Assert.Contains("parse failure", result.Error);
    }

    [Fact]
    public void EditById_Replace_TargetsExactlyTheResolvedActivity() {
        const string workflow = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main">
                <ui:LogMessage DisplayName="Dup" Message="first" />
                <ui:LogMessage DisplayName="Dup" Message="second" />
              </Sequence>
            </Activity>
            """;

        var result = XamlActivityEditor.EditById(workflow, XamlActivityEditor.Replace,
            "sequence.1/logmessage.2", fragment: "<ui:Comment DisplayName=\"Note\" />");

        Assert.True(result.Success, result.Error);
        Assert.Equal("sequence.1/logmessage.2", result.ResolvedId);
        Assert.Contains("Message=\"first\"", result.UpdatedContent);
        Assert.DoesNotContain("Message=\"second\"", result.UpdatedContent);
        Assert.Contains("<ui:Comment DisplayName=\"Note\"", result.UpdatedContent);
    }

    [Fact]
    public void EditById_UnknownId_ReturnsActivityNotFound() {
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove, "sequence.9/nope.1");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityNotFound, result.ErrorCode);
        Assert.Contains("sequence.9/nope.1", result.Error);
    }

    [Fact]
    public void EditById_TypeMismatch_ReturnsActivityIdStale() {
        // sequence.1 is a Sequence; claiming it is a LogMessage means the snapshot moved.
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove,
            "sequence.1", activityType: "LogMessage");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityIdStale, result.ErrorCode);
        Assert.Contains("find_activity", result.Error);
    }

    [Fact]
    public void EditById_DisplayNameMismatch_ReturnsActivityIdStale() {
        var result = XamlActivityEditor.EditById(Workflow, XamlActivityEditor.Remove,
            "sequence.1", expectedDisplayName: "Renamed since snapshot");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityIdStale, result.ErrorCode);
    }

    [Fact]
    public void Edit_NoDisplayNameMatch_CarriesActivityNotFoundCode() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Missing");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.ActivityNotFound, result.ErrorCode);
    }

    [Fact]
    public void Edit_AmbiguousDisplayName_CarriesAmbiguousActivityCode() {
        const string workflow = """
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

        var result = XamlActivityEditor.Edit(workflow, XamlActivityEditor.Remove, "Dup");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.AmbiguousActivity, result.ErrorCode);
        Assert.Contains("activityId", result.Error);
    }

    [Fact]
    public void Edit_ByDisplayName_ReportsResolvedIdOnSuccess() {
        var result = XamlActivityEditor.Edit(Workflow, XamlActivityEditor.Remove, "Start");

        Assert.True(result.Success, result.Error);
        Assert.Equal("sequence.1/logmessage.1", result.ResolvedId);
    }
}

public class EditWorkflowActivityToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence DisplayName="Main" />
        </Activity>
        """;

    private static (FakeFilesystemProvider Fs, EditWorkflowActivityTool Tool, string Target) CreateTool() {
        var fs = new FakeFilesystemProvider();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = Workflow;
        return (fs, new EditWorkflowActivityTool(fs), target);
    }

    [Fact]
    public void EditWorkflowActivity_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove", "Main");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void EditWorkflowActivity_RejectsNonXamlFile() {
        var (_, tool, _) = CreateTool();

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.cs", "remove", "Main");

        Assert.Equal("error", result.Status);
        Assert.Contains(".xaml", result.Summary);
    }

    [Fact]
    public void EditWorkflowActivity_RejectsUnknownOperation() {
        var (_, tool, _) = CreateTool();

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "mutate", "Main");

        Assert.Equal("error", result.Status);
        Assert.Contains("insert, replace, or remove", result.Summary);
    }

    [Fact]
    public void EditWorkflowActivity_WhenFileMissing_ReturnsError() {
        var fs = new FakeFilesystemProvider();
        var tool = new EditWorkflowActivityTool(fs);

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove", "Main");

        Assert.Equal("error", result.Status);
        Assert.Contains("not found", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditWorkflowActivity_RejectsPathOutsideProject() {
        var (_, tool, target) = CreateTool();

        var result = tool.EditWorkflowActivity(ProjectPath, "../Main.xaml", "remove", "Main");

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void EditWorkflowActivity_InsertIntoEmptySequence_WritesUpdatedFile() {
        var (fs, tool, target) = CreateTool();

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "insert", "Main",
            fragment: "<ui:LogMessage DisplayName=\"Hello\" Message=\"hi\" />");
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Equal("insert", data.GetProperty("operation").GetString());
        var written = fs.Writes[target];
        Assert.Contains("<ui:LogMessage DisplayName=\"Hello\" Message=\"hi\"", written);
        Assert.Contains("xmlns:ui=\"http://schemas.uipath.com/workflow/activities\"", written);
        // The once-empty sequence is now a proper container.
        Assert.Contains("<Sequence DisplayName=\"Main\">", written);
    }

    [Fact]
    public void EditWorkflowActivity_WhenTargetNotFound_DoesNotWrite() {
        var (fs, tool, target) = CreateTool();

        var result = tool.EditWorkflowActivity(ProjectPath, "Main.xaml", "remove", "Missing");

        Assert.Equal("error", result.Status);
        Assert.False(fs.Writes.ContainsKey(target));
    }
}
