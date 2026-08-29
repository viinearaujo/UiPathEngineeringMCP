using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectGapAnalyzerTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-gaps-" + Guid.NewGuid().ToString("N"));

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private static WorkflowModel Wf(string fileName, bool isMain = false, string? description = "A workflow.",
        int exceptionHandlers = 0, int logMessages = 0, params string[] invokes) => new() {
        FileName = fileName,
        IsMain = isMain,
        Description = description,
        ExceptionHandlers = Enumerable.Range(0, exceptionHandlers)
            .Select(_ => new ExceptionHandlerModel { WorkflowName = fileName }).ToList(),
        LogMessages = Enumerable.Range(0, logMessages)
            .Select(_ => new LogMessageModel()).ToList(),
        InvokeWorkflows = invokes
            .Select(t => new InvokeWorkflowModel { SourceWorkflow = fileName, TargetWorkflow = t }).ToList()
    };

    // A model that trips no rule: entry point with handling/logging/description,
    // one invoked child, and a (standalone) test workflow.
    private static UiPathProjectModel CleanModel(string? mainWorkflow = "Main.xaml", string projectPath = "") => new() {
        ProjectPath = projectPath,
        ProjectName = "clean",
        MainWorkflow = mainWorkflow,
        Workflows = [
            Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1, invokes: ["Child.xaml"]),
            Wf("Child.xaml"),
            Wf("Tests/TestMain.xaml")
        ]
    };

    [Fact]
    public void Analyze_CleanProject_ReportsNoGaps() {
        var gaps = ProjectGapAnalyzer.Analyze(CleanModel());

        Assert.Empty(gaps);
    }

    [Fact]
    public void Analyze_NoEntryPoint_ReportsError() {
        var gaps = ProjectGapAnalyzer.Analyze(CleanModel(mainWorkflow: null));

        var gap = Assert.Single(gaps, g => g.Id == "no-entry-point");
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal("write_workflow_file", gap.SuggestedTool);
    }

    [Fact]
    public void Analyze_EntryPointDeclaredButMissingOnDisk_ReportsError() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = [Wf("Child.xaml"), Wf("Tests/TestMain.xaml")]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "entry-point-missing" && g.Severity == Gap.Error && g.TargetFile == "Main.xaml");
    }

    [Fact]
    public void Analyze_DeclaredEntryPointFileMissing_ReportsError() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            EntryPoints = ["Coded.cs"],
            Workflows = [Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1), Wf("Tests/TestMain.xaml")]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "declared-entry-point-missing:Coded.cs" && g.Severity == Gap.Error);
    }

    [Fact]
    public void Analyze_OrphanWorkflow_ReportsWarning_ButExemptsTestWorkflows() {
        var model = CleanModel();
        model.Workflows.Add(Wf("Unused.xaml"));

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "orphan-workflow:Unused.xaml" && g.Severity == Gap.Warning);
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("orphan-workflow:Tests/", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_UnresolvedInvocation_ReportsError() {
        var model = CleanModel();
        model.Workflows[0].InvokeWorkflows.Add(new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Missing.xaml" });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "unresolved-invoke:Main.xaml->Missing.xaml" && g.Severity == Gap.Error);
    }

    [Fact]
    public void Analyze_EntryWithoutExceptionHandlingOrLogging_ReportsBoth() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = [Wf("Main.xaml", isMain: true), Wf("Tests/TestMain.xaml")]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "entry-no-exception-handling" && g.Severity == Gap.Warning && g.SuggestedTool == "edit_workflow_activity");
        Assert.Contains(gaps, g => g.Id == "entry-no-logging" && g.Severity == Gap.Info);
    }

    [Fact]
    public void Analyze_WorkflowWithoutDescription_ReportsInfo() {
        var model = CleanModel();
        model.Workflows[1].Description = null;

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "workflow-no-description:Child.xaml" && g.Severity == Gap.Info);
    }

    [Fact]
    public void Analyze_NoTestWorkflows_ReportsInfo() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = [Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1)]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "no-test-workflows" && g.Severity == Gap.Info);
    }

    [Fact]
    public void Analyze_CodedTestFile_SatisfiesTestWorkflowRule() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = [Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1)],
            CodedWorkflows = [new CodedWorkflowModel { FileName = "InvoiceTests.cs" }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id == "no-test-workflows");
    }

    [Fact]
    public void Analyze_PendingTaskWithAllTargetFilesPresent_SuggestsUpdatePlanTask() {
        Directory.CreateDirectory(_projectPath);
        File.WriteAllText(Path.Combine(_projectPath, "Main.xaml"), "<Activity />");
        var model = CleanModel(projectPath: _projectPath);
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.Pending, TargetFiles = ["Main.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan);

        var gap = Assert.Single(gaps, g => g.Id == "plan-task-possibly-complete:task-1");
        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal("update_plan_task", gap.SuggestedTool);
        Assert.Equal(
            "Run validate_project(build:false, pack:false), then update_plan_task for 'task-1' to mark it done.",
            gap.SuggestedAction);
    }

    [Fact]
    public void Analyze_InProgressTaskWithMissingTargetFile_ReportsPlannedArtifactMissing() {
        var model = CleanModel(projectPath: _projectPath);
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.InProgress, TargetFiles = ["Main.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan);

        var gap = Assert.Single(gaps, g => g.Id == "plan-artifact-missing:task-1");
        Assert.Equal(Gap.Warning, gap.Severity);
        Assert.Equal("Main.xaml", gap.TargetFile);
        Assert.Equal("write_workflow_file", gap.SuggestedTool);
    }

    [Fact]
    public void Analyze_DoneTask_IsNotCrossChecked() {
        var model = CleanModel(projectPath: _projectPath);
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.Done, TargetFiles = ["Missing.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan);

        Assert.DoesNotContain(gaps, g => g.Category == "plan");
    }
}
