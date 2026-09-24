using System.ComponentModel.DataAnnotations;
using System.Reflection;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

/// <summary>
/// The enum-like string parameters carry <see cref="AllowedValuesAttribute"/> so the MCP SDK
/// advertises them as JSON-schema <c>enum</c> values and answers <c>completion/complete</c> with
/// them. These tests pin each attribute to the same constants the tool validates against in
/// <c>ToolArgs.ParseChoice</c>, so the advertised values cannot silently drift from the accepted
/// ones.
/// </summary>
public class AllowedValuesAttributeTests {
    private static string[] AllowedValuesOf(string toolMethod, string parameterName) {
        var method = ToolMethod(toolMethod);
        var parameter = method.GetParameters().Single(p => p.Name == parameterName);
        var attribute = parameter.GetCustomAttribute<AllowedValuesAttribute>();
        Assert.NotNull(attribute);
        return attribute!.Values.Select(v => Assert.IsType<string>(v)).ToArray();
    }

    private static MethodInfo ToolMethod(string name) =>
        typeof(CreateProjectTool).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Single(m => m.Name == name);

    [Fact]
    public void CreateProject_AdvertisesExpressionLanguageAndTargetFramework() {
        Assert.Equal(["CSharp", "VisualBasic"], AllowedValuesOf(nameof(CreateProjectTool.CreateProject), "expressionLanguage"));
        Assert.Equal(["Windows", "Portable"], AllowedValuesOf(nameof(CreateProjectTool.CreateProject), "targetFramework"));
    }

    [Fact]
    public void AddCodedWorkflow_AdvertisesKind() {
        Assert.Equal(
            [CodedFileKind.Workflow, CodedFileKind.Test, CodedFileKind.Source],
            AllowedValuesOf(nameof(CreateCodedWorkflowTool.AddCodedWorkflow), "kind"));
    }

    [Fact]
    public void AnalyzeProject_AdvertisesDetail() {
        Assert.Equal(
            [ProjectAnalysisView.DetailSummary, ProjectAnalysisView.DetailFull],
            AllowedValuesOf(nameof(AnalyzeProjectTool.AnalyzeProject), "detail"));
    }

    [Fact]
    public void ManageWorkflowData_AdvertisesOperationAndKind() {
        Assert.Equal(
            [WorkflowSurfaceEditor.Add, WorkflowSurfaceEditor.Remove, WorkflowSurfaceEditor.Rename],
            AllowedValuesOf(nameof(ManageWorkflowDataTool.ManageWorkflowData), "operation"));
        Assert.Equal(
            [WorkflowSurfaceEditor.Variable, WorkflowSurfaceEditor.Argument],
            AllowedValuesOf(nameof(ManageWorkflowDataTool.ManageWorkflowData), "kind"));
    }

    [Fact]
    public void InsertActivities_And_EditWorkflowActivity_AdvertisePositionAndOperation() {
        string[] positions = [XamlActivityEditor.First, XamlActivityEditor.Last];
        Assert.Equal(positions, AllowedValuesOf(nameof(InsertActivitiesTool.InsertActivities), "position"));
        Assert.Equal(positions, AllowedValuesOf(nameof(EditWorkflowActivityTool.EditWorkflowActivity), "position"));
        Assert.Equal(
            [XamlActivityEditor.Insert, XamlActivityEditor.Replace, XamlActivityEditor.Remove],
            AllowedValuesOf(nameof(EditWorkflowActivityTool.EditWorkflowActivity), "operation"));
    }

    [Fact]
    public void UpdatePlanTask_AdvertisesStatus() {
        Assert.Equal(
            [PlanTask.Pending, PlanTask.InProgress, PlanTask.Done, PlanTask.Blocked],
            AllowedValuesOf(nameof(UpdatePlanTaskTool.UpdatePlanTask), "status"));
    }

    [Fact]
    public void ManageProjectFile_AdvertisesAction() {
        Assert.Equal(
            [ManageProjectFileTool.Write, ManageProjectFileTool.Edit, ManageProjectFileTool.Delete],
            AllowedValuesOf(nameof(ManageProjectFileTool.ManageProjectFile), "action"));
    }

    [Fact]
    public void ManageProjectDocs_AdvertisesActionAndKind() {
        Assert.Equal(
            [ManageProjectDocsTool.List, ManageProjectDocsTool.Write, ManageProjectDocsTool.Delete, ManageProjectDocsTool.Search],
            AllowedValuesOf(nameof(ManageProjectDocsTool.ManageProjectDocs), "action"));
        Assert.Equal(
            [ProjectKnowledgeStore.Kind, ProjectAdrStore.Kind, ManageProjectDocsTool.ContextKind, ProjectDocsSearch.KindAll],
            AllowedValuesOf(nameof(ManageProjectDocsTool.ManageProjectDocs), "kind"));
    }

    [Fact]
    public void SearchCodebase_AdvertisesModeAndKind() {
        Assert.Equal(
            ["text", "symbol", "activity", "workflow"],
            AllowedValuesOf(nameof(SearchCodebaseTool.SearchCodebase), "mode"));
        Assert.Equal(
            ["method", "property", "field", "class", "interface"],
            AllowedValuesOf(nameof(SearchCodebaseTool.SearchCodebase), "kind"));
    }

    [Fact]
    public void GetCompileErrors_And_FindCodeSymbol_AdvertiseTheirFilters() {
        Assert.Equal(["error", "warning", "all"], AllowedValuesOf(nameof(GetCompileErrorsTool.GetCompileErrors), "severity"));
        Assert.Equal(
            ["method", "property", "field", "class", "interface"],
            AllowedValuesOf(nameof(FindCodeSymbolTool.FindCodeSymbol), "kind"));
    }

    [Fact]
    public void ManagePackages_And_PatchProjectJson_AdvertiseOperation() {
        Assert.Equal(
            ["install", "versions", "inspect"],
            AllowedValuesOf(nameof(ManagePackagesTool.ManagePackages), "operation"));
        Assert.Equal(
            [
                ProjectJsonPatcher.AddEntryPoint,
                ProjectJsonPatcher.RemoveEntryPoint,
                ProjectJsonPatcher.UpsertDependency,
                ProjectJsonPatcher.RemoveDependency,
                ProjectJsonPatcher.UpsertFileInfo,
                ProjectJsonPatcher.RemoveFileInfo,
                ProjectJsonPatcher.SetExceptionHandler,
                ProjectJsonPatcher.SetRuntimeOption
            ],
            AllowedValuesOf(nameof(PatchProjectJsonTool.PatchProjectJson), "operation"));
    }

    [Fact]
    public void GetObjectRepository_AdvertisesSource() {
        Assert.Equal(["project", "library"], AllowedValuesOf(nameof(GetObjectRepositoryTool.GetObjectRepository), "source"));
    }

    [Fact]
    public void GetAnalyzerRules_AdvertisesScopeAndSeverity() {
        Assert.Equal(
            [.. CliVerbArguments.AnalyzerRuleScopes, "All"],
            AllowedValuesOf(nameof(GetAnalyzerRulesTool.GetAnalyzerRules), "scope"));
        Assert.Equal(
            ["error", "warning", "info"],
            AllowedValuesOf(nameof(GetAnalyzerRulesTool.GetAnalyzerRules), "minSeverity"));
    }

    [Fact]
    public void RunWorkflow_And_ControlDebugSession_AdvertiseLogLevelAndProfilingMode() {
        string[] logLevels = [.. CliVerbArguments.RunLogLevels];
        string[] profilingModes = [.. CliVerbArguments.ProfilingModes];

        Assert.Equal(logLevels, AllowedValuesOf(nameof(RunWorkflowTool.RunWorkflow), "logLevel"));
        Assert.Equal(profilingModes, AllowedValuesOf(nameof(RunWorkflowTool.RunWorkflow), "profilingMode"));
        Assert.Equal(logLevels, AllowedValuesOf(nameof(ControlDebugSessionTool.ControlDebugSession), "logLevel"));
        Assert.Equal(profilingModes, AllowedValuesOf(nameof(ControlDebugSessionTool.ControlDebugSession), "profilingMode"));
    }

    [Fact]
    public void ControlDebugSession_AdvertisesEveryAcceptedCommand() {
        var advertised = AllowedValuesOf(nameof(ControlDebugSessionTool.ControlDebugSession), "command");
        // start, then the mid-session verbs, set-breakpoints, and cancel.
        Assert.Equal(
            [
                ControlDebugSessionTool.StartCommand,
                .. CliVerbArguments.DebugSessionCommands,
                ControlDebugSessionTool.SetBreakpointsCommand,
                CliVerbArguments.CancelCommand
            ],
            advertised);
    }
}