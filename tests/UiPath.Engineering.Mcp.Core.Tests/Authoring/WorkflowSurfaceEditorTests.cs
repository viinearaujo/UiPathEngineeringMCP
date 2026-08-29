using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class WorkflowSurfaceEditorTests {
    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="Say" Text="hi" />
          </Sequence>
        </Activity>
        """;

    private const string WorkflowWithMembers = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <x:Members>
          </x:Members>
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="Say" Text="hi" />
          </Sequence>
        </Activity>
        """;

    private const string WorkflowWithVariable = """
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

    [Fact]
    public void Edit_AddVariableToSequenceWithoutVariablesBlock_CreatesBlockAndVariable() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "count", type: "Int32");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Sequence.Variables>", result.UpdatedContent);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"count\"", result.UpdatedContent);
        Assert.Contains("</Sequence.Variables>", result.UpdatedContent);
    }

    [Fact]
    public void Edit_AddVariableWithDefault_RendersDefaultAttribute() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "message",
            type: "String", defaultValue: "hello");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Variable x:TypeArguments=\"x:String\" Name=\"message\" Default=\"hello\"",
            result.UpdatedContent);
    }

    [Fact]
    public void Edit_AddVariableWithFullTypeName_KeepsBareToken() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "table",
            type: "System.Data.DataTable");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Variable x:TypeArguments=\"System.Data.DataTable\" Name=\"table\"",
            result.UpdatedContent);
    }

    [Fact]
    public void Edit_AddDuplicateVariable_DeclarationConflict() {
        var result = WorkflowSurfaceEditor.Edit(WorkflowWithVariable, "add", "variable", "message",
            type: "String");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.DataDeclarationConflict, result.ErrorCode);
    }

    [Fact]
    public void Edit_RemoveVariable_RemovesDeclaration() {
        var result = WorkflowSurfaceEditor.Edit(WorkflowWithVariable, "remove", "variable", "message");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("Name=\"message\"", result.UpdatedContent);
    }

    [Fact]
    public void Edit_RemoveMissingVariable_NotFound() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "remove", "variable", "missing");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.DataDeclarationNotFound, result.ErrorCode);
    }

    [Fact]
    public void Edit_AddInArgument_InsertsPropertyInsideMembers() {
        var result = WorkflowSurfaceEditor.Edit(WorkflowWithMembers, "add", "argument", "input",
            type: "String", direction: "In");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<x:Members>", result.UpdatedContent);
        var membersStart = result.UpdatedContent!.IndexOf("<x:Members>", StringComparison.Ordinal);
        var membersEnd = result.UpdatedContent.IndexOf("</x:Members>", StringComparison.Ordinal);
        var members = result.UpdatedContent[membersStart..membersEnd];
        Assert.Contains("<x:Property Name=\"input\" Type=\"InArgument(x:String)\" />", members);
        var afterMembers = result.UpdatedContent[membersEnd..];
        Assert.DoesNotContain("<x:Property Name=\"input\"", afterMembers);
    }

    [Fact]
    public void Edit_AddInArgument_CreatesMembersWhenMissing() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "argument", "input",
            type: "String", direction: "In");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<x:Members>", result.UpdatedContent);
        var membersStart = result.UpdatedContent!.IndexOf("<x:Members>", StringComparison.Ordinal);
        var membersEnd = result.UpdatedContent.IndexOf("</x:Members>", StringComparison.Ordinal);
        Assert.Contains("<x:Property Name=\"input\" Type=\"InArgument(x:String)\" />",
            result.UpdatedContent[membersStart..membersEnd]);
    }

    [Fact]
    public void Edit_AddOutArgument_RendersOutArgumentProperty() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "argument", "result",
            type: "Int32", direction: "Out");

        Assert.True(result.Success, result.Error);
        var membersStart = result.UpdatedContent!.IndexOf("<x:Members>", StringComparison.Ordinal);
        var membersEnd = result.UpdatedContent.IndexOf("</x:Members>", StringComparison.Ordinal);
        Assert.True(membersStart >= 0 && membersEnd > membersStart);
        Assert.Contains("<x:Property Name=\"result\" Type=\"OutArgument(x:Int32)\" />",
            result.UpdatedContent[membersStart..membersEnd]);
    }

    [Fact]
    public void Edit_AddInOutArgument_RendersInOutArgumentProperty() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "argument", "buffer",
            type: "String", direction: "In/Out");

        Assert.True(result.Success, result.Error);
        var membersStart = result.UpdatedContent!.IndexOf("<x:Members>", StringComparison.Ordinal);
        var membersEnd = result.UpdatedContent.IndexOf("</x:Members>", StringComparison.Ordinal);
        Assert.True(membersStart >= 0 && membersEnd > membersStart);
        Assert.Contains("<x:Property Name=\"buffer\" Type=\"InOutArgument(x:String)\" />",
            result.UpdatedContent[membersStart..membersEnd]);
    }

    [Fact]
    public void Edit_AddDuplicateArgument_DeclarationConflict() {
        var withArg = Workflow.Replace(
            "<Sequence DisplayName=\"Main\">",
            "<x:Members>\n    <x:Property Name=\"input\" Type=\"InArgument(x:String)\" />\n  </x:Members>\n  <Sequence DisplayName=\"Main\">");

        var result = WorkflowSurfaceEditor.Edit(withArg, "add", "argument", "input",
            type: "String", direction: "In");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.DataDeclarationConflict, result.ErrorCode);
    }

    [Fact]
    public void Edit_RemoveArgument_RemovesDeclaration() {
        var withArg = Workflow.Replace(
            "<Sequence DisplayName=\"Main\">",
            "<x:Property Name=\"input\" Type=\"InArgument(x:String)\" />\n  <Sequence DisplayName=\"Main\">");

        var result = WorkflowSurfaceEditor.Edit(withArg, "remove", "argument", "input");

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("Name=\"input\"", result.UpdatedContent);
    }

    [Fact]
    public void Edit_RenameVariable_ReturnsUsageWarning() {
        var result = WorkflowSurfaceEditor.Edit(WorkflowWithVariable, "rename", "variable",
            "message", newName: "greeting");

        Assert.True(result.Success, result.Error);
        Assert.Contains("Name=\"greeting\"", result.UpdatedContent);
        Assert.DoesNotContain("Name=\"message\"", result.UpdatedContent);
        // Expressions are not rewritten; the warning must say so.
        Assert.Contains(result.Warnings, w => w.Contains("message") && w.Contains("greeting"));
        Assert.Contains("[message]", result.UpdatedContent);
    }

    [Fact]
    public void Edit_RenameMissingArgument_NotFound() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "rename", "argument",
            "missing", newName: "other");

        Assert.False(result.Success);
        Assert.Equal(ToolErrorCodes.DataDeclarationNotFound, result.ErrorCode);
    }

    [Fact]
    public void Edit_WhenWorkflowMalformed_ReturnsError() {
        var result = WorkflowSurfaceEditor.Edit("<Activity", "add", "variable", "x", type: "String");

        Assert.False(result.Success);
        Assert.Contains("parse failure", result.Error);
    }

    [Fact]
    public void Edit_WhenOperationUnknown_ReturnsError() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "mutate", "variable", "x", type: "String");

        Assert.False(result.Success);
        Assert.Contains("operation", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Edit_WhenKindUnknown_ReturnsError() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "parameter", "x", type: "String");

        Assert.False(result.Success);
        Assert.Contains("kind", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
