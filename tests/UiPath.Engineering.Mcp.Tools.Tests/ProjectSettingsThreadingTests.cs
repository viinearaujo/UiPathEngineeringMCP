using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests;

/// <summary>
/// The project's expressionLanguage decides the XAML binding form, so it has to
/// reach the validator and the builder from project.json. These tests drive the
/// three spec-accepting tools with a hand-written project-model fake and assert
/// the rendered output, plus the no-project fallback behavior.
/// </summary>
public class ProjectSettingsThreadingTests {
    private const string ProjectPath = "/projects/testProcess";

    private static FakeFilesystemProvider Filesystem() => new();

    private static FakeProjectModelBuilder Builder(string? expressionLanguage, string? targetFramework = "Windows") =>
        new() {
            Model = new UiPathProjectModel {
                ProjectName = "testProcess",
                ExpressionLanguage = expressionLanguage,
                TargetFramework = targetFramework
            }
        };

    private static JsonElement Data(ToolResult result) =>
        JsonSerializer.SerializeToElement(result.Data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // ---- build_workflow --------------------------------------------------------

    [Fact]
    public async Task BuildWorkflow_CSharpProject_RendersCSharpBindingsNotBrackets() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "\"count: \" + n", "level": "Info" } }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
        var xaml = fs.Writes.Values.Single();
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Object\">\"count: \" + n</CSharpValue>", xaml);
        Assert.Contains("<TextExpression.NamespacesForImplementation>", xaml);
        Assert.DoesNotContain("VisualBasic.Settings", xaml);
        Assert.Equal("CSharp", Data(result).GetProperty("expressionLanguage").GetString());
    }

    [Fact]
    public async Task BuildWorkflow_VisualBasicProject_KeepsBracketAttributeForm() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs), Builder("VisualBasic"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
        var xaml = fs.Writes.Values.Single();
        Assert.Contains("Message=\"[msg]\"", xaml);
        Assert.Contains("<VisualBasic.Settings>", xaml);
        Assert.DoesNotContain("CSharpValue", xaml);
        Assert.Equal("VisualBasic", Data(result).GetProperty("expressionLanguage").GetString());
    }

    [Fact]
    public async Task BuildWorkflow_CSharpProject_RejectsBracketShorthand() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch
            && e.Message.Contains("C#-expression project"));
        Assert.Empty(fs.Writes);
    }

    [Fact]
    public async Task BuildWorkflow_NoProjectModelBuilder_FallsBackToVisualBasicBehavior() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
        Assert.Contains("Message=\"[msg]\"", fs.Writes.Values.Single());
    }

    [Fact]
    public async Task BuildWorkflow_LegacyTargetFramework_UsesMscorlibAliases() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs), Builder("VisualBasic", "Legacy"));
        const string spec = """
            { "name": "InvokeWorkflowFile", "properties": { "workflowFileName": "Child.xaml" },
              "arguments": [{ "name": "in_Path", "direction": "In", "type": "String", "value": "[p]" }] }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
        var xaml = fs.Writes.Values.Single();
        Assert.Contains("clr-namespace:System.Collections.Generic;assembly=mscorlib", xaml);
        Assert.DoesNotContain("System.Private.CoreLib", xaml);
    }

    [Fact]
    public async Task BuildWorkflow_UnreadableProject_DoesNotFailTheTool() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs),
            new FakeProjectModelBuilder { ToThrow = new FileNotFoundException() });
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
    }

    [Fact]
    public async Task BuildWorkflow_UnknownProperty_SurfacesARenderWarning() {
        var fs = Filesystem();
        var tool = new BuildWorkflowTool(fs, TestCatalogs.Resolver(fs), Builder("VisualBasic"));
        const string spec = """
            { "name": "Sequence", "properties": { "displayName": "Steps", "Bogus": "1" },
              "children": [{ "name": "Rethrow" }] }
            """;

        var result = await tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", spec);

        Assert.Equal("success", result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("Bogus", StringComparison.Ordinal));
    }

    // ---- validate_activity_spec ------------------------------------------------

    [Fact]
    public async Task ValidateActivitySpec_CSharpProject_AppliesCSharpFormRules() {
        var tool = new ValidateActivitySpecTool(TestCatalogs.Resolver(Filesystem()), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.ValidateActivitySpec(spec, ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public async Task ValidateActivitySpec_WithoutProjectPath_UsesVisualBasicForms() {
        var tool = new ValidateActivitySpecTool(TestCatalogs.Resolver(Filesystem()), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.ValidateActivitySpec(spec);

        Assert.Equal("success", result.Status);
        Assert.Equal("VisualBasic", Data(result).GetProperty("expressionLanguage").GetString());
    }

    [Fact]
    public async Task ValidateActivitySpec_CSharpProject_ReportsLanguageAndValidRawExpressions() {
        var tool = new ValidateActivitySpecTool(TestCatalogs.Resolver(Filesystem()), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "\"a\" + b", "level": "Info" } }
            """;

        var result = await tool.ValidateActivitySpec(spec, ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Equal("CSharp", Data(result).GetProperty("expressionLanguage").GetString());
    }

    // ---- insert_activities -----------------------------------------------------

    [Fact]
    public async Task InsertActivities_CSharpProject_InsertsCSharpBindings() {
        var fs = Filesystem();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
              xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <Sequence DisplayName="Main" />
            </Activity>
            """;
        var tool = new InsertActivitiesTool(fs, TestCatalogs.Resolver(fs), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "\"a\" + b", "level": "Info" } }
            """;

        var result = await tool.InsertActivities(ProjectPath, "Main.xaml", spec, displayName: "Main");

        Assert.Equal("success", result.Status);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Object\">\"a\" + b</CSharpValue>", fs.Writes[target]);
        Assert.Equal("CSharp", Data(result).GetProperty("expressionLanguage").GetString());
    }

    [Fact]
    public async Task InsertActivities_CSharpProject_RejectsBracketShorthand() {
        var fs = Filesystem();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main" />
            </Activity>
            """;
        var tool = new InsertActivitiesTool(fs, TestCatalogs.Resolver(fs), Builder("CSharp"));
        const string spec = """
            { "name": "LogMessage", "properties": { "message": "[msg]", "level": "Info" } }
            """;

        var result = await tool.InsertActivities(ProjectPath, "Main.xaml", spec, displayName: "Main");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public async Task InsertActivities_RootSequenceChildren_StillInsertWithoutAWrapper() {
        var fs = Filesystem();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = """
            <Activity x:Class="Main"
              xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
              xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence DisplayName="Main" />
            </Activity>
            """;
        var tool = new InsertActivitiesTool(fs, TestCatalogs.Resolver(fs), Builder("CSharp"));
        const string spec = """
            { "name": "Sequence", "children": [
                { "name": "Assign", "properties": { "to": "a", "value": "1", "typeArgument": "Int32" } },
                { "name": "Rethrow" } ] }
            """;

        var result = await tool.InsertActivities(ProjectPath, "Main.xaml", spec, displayName: "Main");

        Assert.Equal("success", result.Status);
        var written = fs.Writes[target];
        // One Sequence (the target), not an inserted wrapper.
        Assert.Equal(1, Count(written, "<Sequence"));
        Assert.Contains("<Assign x:TypeArguments=\"x:Int32\"", written);
        Assert.Contains("<CSharpReference x:TypeArguments=\"x:Int32\">a</CSharpReference>", written);
        Assert.Contains("<CSharpValue x:TypeArguments=\"x:Int32\">1</CSharpValue>", written);
    }

    private static int Count(string text, string needle) {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
