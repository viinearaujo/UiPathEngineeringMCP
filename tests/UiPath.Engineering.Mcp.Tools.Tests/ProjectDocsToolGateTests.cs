using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ProjectDocsToolGateTests {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-docs-gate-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs;

    public ProjectDocsToolGateTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new FakeFilesystemProvider { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    [Fact]
    public async Task ValidateProjectDocs_ReportsMissingContext() {
        var tool = new ValidateProjectDocsTool(_fs, new FakeProjectModelBuilder(), DocsSupport.Validator(_fs));

        var result = await tool.ValidateProjectDocs(_projectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.DocsStale);
    }

    [Fact]
    public async Task SyncProjectContext_WritesGeneratedFiles() {
        var tool = new SyncProjectContextTool(_fs, new FakeProjectModelBuilder(), DocsSupport.Renderer(_fs));

        var result = await tool.SyncProjectContext(_projectPath);

        Assert.Equal("success", result.Status);
        Assert.True(_fs.FileExists(UiPath.Engineering.Mcp.Core.Docs.ProjectDocsPaths.AgentsMd(_projectPath)));
        Assert.True(_fs.FileExists(UiPath.Engineering.Mcp.Core.Docs.ProjectDocsPaths.ProjectContext(_projectPath)));
    }

    [Fact]
    public async Task VerifyWork_DoesNotMarkDoneOnDocsErrors() {
        var store = new UiPath.Engineering.Mcp.Core.Planning.ImplementationPlanStore();
        store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main workflow", TargetFiles = ["Main.xaml"] }]
        });
        _fs.ExistingFiles.Add(Path.Combine(Path.GetFullPath(_projectPath), "Main.xaml"));
        var cli = new FakeUiPathCliProvider { Result = new UiPathCliResult { Success = true, Summary = "ok" } };
        var tool = new VerifyWorkTool(_fs, new FakeProjectModelBuilder(), cli, store, DocsSupport.Validator(_fs));

        var result = await tool.VerifyWork(_projectPath, ["task-1"]);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.DocsStale);
        Assert.Equal(PlanTask.Pending, store.Load(_projectPath)!.Tasks[0].Status);
    }

    [Fact]
    public async Task AnalyzeProjectGaps_DocsErrorAppearsAsGap() {
        var store = new UiPath.Engineering.Mcp.Core.Planning.ImplementationPlanStore();
        var model = new UiPathProjectModel {
            ProjectPath = _projectPath,
            ProjectName = "clean",
            MainWorkflow = "Main.xaml",
            Workflows = [
                new WorkflowModel {
                    FileName = "Main.xaml",
                    IsMain = true,
                    Description = "Entry point.",
                    ExceptionHandlers = [new ExceptionHandlerModel { WorkflowName = "Main.xaml" }],
                    LogMessages = [new LogMessageModel()],
                    InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Child.xaml" }]
                },
                new WorkflowModel { FileName = "Child.xaml", Description = "Child." },
                new WorkflowModel { FileName = "Tests/TestMain.xaml", Description = "Tests." }
            ]
        };
        var tool = new AnalyzeProjectGapsTool(_fs, new FakeProjectModelBuilder { Model = model }, store, DocsSupport.Validator(_fs));

        var result = await tool.AnalyzeProjectGaps(_projectPath);
        var data = JsonSerializer.SerializeToElement(result.Data);

        Assert.Equal("success", result.Status);
        Assert.Contains(data.GetProperty("gaps").EnumerateArray(),
            g => g.GetProperty("Category").GetString() == "docs");
    }
}
