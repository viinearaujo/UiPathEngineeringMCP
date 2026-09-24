using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectGapAnalyzerTests {
    private const string ProjectPath = @"C:\projects\clean";

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
    private static UiPathProjectModel CleanModel(string? mainWorkflow = "Main.xaml", string projectPath = ProjectPath) => new() {
        ProjectPath = projectPath,
        ProjectName = "clean",
        MainWorkflow = mainWorkflow,
        Workflows = [
            Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1, invokes: ["Child.xaml"]),
            Wf("Child.xaml"),
            Wf("Tests/TestMain.xaml")
        ]
    };

    /// <summary>In-memory filesystem for the plan cross-check; no temp directory, no disk.</summary>
    private static FakeFilesystemProvider FilesWith(params string[] relativePaths) {
        var filesystem = new FakeFilesystemProvider();
        foreach (var relativePath in relativePaths) {
            filesystem.FileContents[ProjectFilePolicy.CombineProject(ProjectPath, relativePath)] = "<Activity />";
        }

        return filesystem;
    }

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
        Assert.Equal("add_coded_workflow", gap.SuggestedTool);
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

    [Theory]
    [InlineData("Latest.xaml")]
    [InlineData("LatestInvoice.xaml")]
    [InlineData("Attest.xaml")]
    [InlineData("Contest.xaml")]
    [InlineData("Protest.xaml")]
    public void Analyze_ProseWordEndingInTest_IsNotClassifiedAsTest(string fileName) {
        // A plain EndsWith("Test", IgnoreCase) exempted these real production workflows from
        // orphan detection and counted them as test coverage. They must be flagged as orphans.
        var model = CleanModel();
        model.Workflows.Add(Wf(fileName, description: "A real workflow."));

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == $"orphan-workflow:{fileName}");
    }

    [Theory]
    [InlineData("TestLoginFlow.xaml")]
    [InlineData("InvoiceTests.xaml")]
    [InlineData("Invoice_Test.xaml")]
    [InlineData("test_invoice.xaml")]
    public void Analyze_RealTestWorkflowName_IsExemptFromOrphanRule(string fileName) {
        var model = CleanModel();
        model.Workflows.Add(Wf(fileName, description: "A test case."));

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Id == $"orphan-workflow:{fileName}");
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

        Assert.Contains(gaps, g => g.Id == "entry-no-exception-handling" && g.Severity == Gap.Warning && g.SuggestedTool == "insert_activities");
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
            CodedWorkflows = [new CodedWorkflowModel { FileName = "InvoiceTests.cs", Kind = CodedFileKind.Test, IsCodedWorkflow = true }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id == "no-test-workflows");
    }

    [Fact]
    public void Analyze_CodedSourceNamedTest_DoesNotSatisfyTestWorkflowRule() {
        var model = new UiPathProjectModel {
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = [Wf("Main.xaml", isMain: true, exceptionHandlers: 1, logMessages: 1)],
            CodedWorkflows = [new CodedWorkflowModel { FileName = "InvoiceTests.cs", Kind = CodedFileKind.Source }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Id == "no-test-workflows");
    }

    [Fact]
    public void Analyze_XamlInvokeOfCodedWorkflowWithCustomType_ReportsBoundaryError() {
        var model = CleanModel();
        model.Workflows[0].InvokeWorkflows.Add(new InvokeWorkflowModel {
            SourceWorkflow = "Main.xaml",
            TargetWorkflow = "InvoiceFlow.cs",
            ArgumentMappings = [
                new ArgumentMappingModel { Direction = "In", TargetArgument = "in_Customer", Type = "CustomerRecord" }
            ]
        });
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "InvoiceFlow.cs",
            ClassName = "InvoiceFlow",
            Kind = CodedFileKind.Workflow,
            IsCodedWorkflow = true
        });
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "CustomerRecord.cs",
            ClassName = "CustomerRecord",
            Kind = CodedFileKind.Source
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Category == "boundary" && g.Severity == Gap.Error && g.Id.Contains("in_Customer"));
    }

    [Fact]
    public void Analyze_PendingTaskWithAllTargetFilesPresent_SuggestsUpdatePlanTask() {
        var model = CleanModel();
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.Pending, TargetFiles = ["Main.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan, filesystem: FilesWith("Main.xaml"));

        var gap = Assert.Single(gaps, g => g.Id == "plan-task-possibly-complete:task-1");
        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Equal("update_plan_task", gap.SuggestedTool);
        Assert.Equal(
            "Run validate_project(build:false, pack:false), then update_plan_task for 'task-1' to mark it done.",
            gap.SuggestedAction);
    }

    [Fact]
    public void Analyze_InProgressTaskWithMissingTargetFile_ReportsPlannedArtifactMissing() {
        var model = CleanModel();
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.InProgress, TargetFiles = ["Main.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan, filesystem: FilesWith());

        var gap = Assert.Single(gaps, g => g.Id == "plan-artifact-missing:task-1");
        Assert.Equal(Gap.Warning, gap.Severity);
        Assert.Equal("Main.xaml", gap.TargetFile);
        Assert.Equal("add_coded_workflow", gap.SuggestedTool);
    }

    [Fact]
    public void Analyze_PlanWithoutFilesystem_SkipsArtifactCrossCheck() {
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.Pending, TargetFiles = ["Main.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(CleanModel(), plan);

        Assert.DoesNotContain(gaps, g => g.Category == "plan");
    }

    [Fact]
    public void Analyze_PlanTargetOutsideProject_CountsAsMissing() {
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Escape", Status = PlanTask.Pending, TargetFiles = ["../escape/Main.xaml"] }]
        };

        var filesystem = new FakeFilesystemProvider { Allowed = false };
        filesystem.FileContents[ProjectFilePolicy.CombineProject(ProjectPath, "../escape/Main.xaml")] = "<Activity />";

        var gaps = ProjectGapAnalyzer.Analyze(CleanModel(), plan, filesystem: filesystem);

        Assert.Single(gaps, g => g.Id == "plan-artifact-missing:task-1");
    }

    [Fact]
    public void Analyze_DoneTask_IsNotCrossChecked() {
        var model = CleanModel();
        var plan = new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main", Status = PlanTask.Done, TargetFiles = ["Missing.xaml"] }]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model, plan, filesystem: FilesWith());

        Assert.DoesNotContain(gaps, g => g.Category == "plan");
    }

    [Fact]
    public void Analyze_DocsErrorFinding_AppearsAsDocsGap() {
        var findings = new List<DocsFinding> {
            new() {
                Code = ToolErrorCodes.DocsStale,
                Severity = DocsFinding.Error,
                Message = "Generated project context is missing.",
                SuggestedTool = "sync_project_context",
                FixHint = "Call sync_project_context."
            }
        };

        var gaps = ProjectGapAnalyzer.Analyze(CleanModel(), docsFindings: findings);

        var gap = Assert.Single(gaps, g => g.Category == "docs");
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal("sync_project_context", gap.SuggestedTool);
    }

    [Fact]
    public void Analyze_CodedWorkflowWithoutTryOrLog_ReportsBoth() {
        var model = CleanModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "InvoiceFlow.cs",
            Kind = CodedFileKind.Workflow,
            IsCodedWorkflow = true,
            EntryMethods = ["Execute"],
            EntryHasTryCatch = false,
            EntryHasLog = false
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        var resilience = Assert.Single(gaps, g => g.Id == "coded-no-exception-handling:InvoiceFlow.cs");
        Assert.Equal(Gap.Warning, resilience.Severity);
        Assert.Equal("resilience", resilience.Category);
        Assert.Equal("edit_workflow_file", resilience.SuggestedTool);
        var observability = Assert.Single(gaps, g => g.Id == "coded-no-logging:InvoiceFlow.cs");
        Assert.Equal(Gap.Info, observability.Severity);
        Assert.Equal("observability", observability.Category);
        Assert.Equal("edit_workflow_file", observability.SuggestedTool);
    }

    [Fact]
    public void Analyze_CodedWorkflowWithTryAndLog_DoesNotReportCodedIdiomGaps() {
        var model = CleanModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "InvoiceFlow.cs",
            Kind = CodedFileKind.Workflow,
            IsCodedWorkflow = true,
            EntryMethods = ["Execute"],
            EntryHasTryCatch = true,
            EntryHasLog = true
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("coded-no-", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CodedSourceAndTest_DoNotReportCodedIdiomGaps() {
        var model = CleanModel();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "Helpers.cs",
            Kind = CodedFileKind.Source,
            EntryHasTryCatch = false,
            EntryHasLog = false
        });
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "InvoiceTests.cs",
            Kind = CodedFileKind.Test,
            IsCodedWorkflow = true,
            EntryHasTryCatch = false,
            EntryHasLog = false
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("coded-no-", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_NonFrameworkXamlWithExcel_PrefersCodedWorkflow() {
        var model = CleanModel();
        model.Workflows.Add(new WorkflowModel {
            FileName = "Process.xaml",
            Description = "Process.",
            Activities = [new ActivityModel { Type = "ReadRangeX", DisplayName = "Read invoices" }]
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        var gap = Assert.Single(gaps, g => g.Id == "xaml-business-logic:Process.xaml");
        // Downgraded from warning: activity types are matched on namespace-blind local names,
        // so this is a preference to verify, not a violation the agent must remediate.
        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
        Assert.Equal("boundary", gap.Category);
        Assert.Equal("add_coded_workflow", gap.SuggestedTool);
        Assert.Contains("prefer a coded workflow", gap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hint", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_XamlBusinessLogicSubstringMatch_IsLowConfidenceHint() {
        var model = CleanModel();
        model.Workflows.Add(new WorkflowModel {
            FileName = "Portal.xaml",
            Description = "Portal.",
            // "Http" is a substring of a custom activity name, not an HTTP call.
            Activities = [new ActivityModel { Type = "NFindElement", DisplayName = "Find portal button" }]
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        var gap = Assert.Single(gaps, g => g.Id == "xaml-business-logic:Portal.xaml");
        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceLow, gap.Confidence);
    }

    [Fact]
    public void Analyze_MainAndFrameworkXamlWithExcel_DoesNotPreferCoded() {
        var model = CleanModel();
        model.Workflows[0].Activities.Add(new ActivityModel { Type = "ReadRangeX", DisplayName = "Read in Main" });
        model.Workflows.Add(new WorkflowModel {
            FileName = "InitAllSettings.xaml",
            FilePath = "/p/Framework/InitAllSettings.xaml",
            Description = "Init.",
            Activities = [new ActivityModel { Type = "HttpClient", DisplayName = "GET config" }]
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("xaml-business-logic:", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_XamlWithOnlyInvokeAndLog_DoesNotPreferCoded() {
        var model = CleanModel();
        model.Workflows.Add(new WorkflowModel {
            FileName = "Dispatcher.xaml",
            Description = "Dispatcher shell.",
            Activities = [
                new ActivityModel { Type = "Sequence", DisplayName = "Body" },
                new ActivityModel { Type = "InvokeWorkflowFile", DisplayName = "Call coded" },
                new ActivityModel { Type = "LogMessage", DisplayName = "Log" }
            ]
        });

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("xaml-business-logic:", StringComparison.Ordinal));
    }
}
