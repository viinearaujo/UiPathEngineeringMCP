using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

/// <summary>
/// The readability category: DisplayName quality, the in_/out_/io_ argument convention,
/// complexity metrics, Rule 24 container body wraps, description coverage, and REFramework
/// conformance. Every rule reads the parsed model only, so a type-name match is graded
/// medium or low confidence and worded as a hint.
/// </summary>
public class ReadabilityGapTests {
    /// <summary>
    /// Builds a workflow whose <see cref="WorkflowModel.Activities"/> is the flat pre-order
    /// list and whose <see cref="ActivityModel.Children"/> mirror it — exactly the shape
    /// <c>XamlWorkflowParser</c> produces. <see cref="ActivityModel.Depth"/> is computed
    /// during the walk, as the parser does, so callers never state it by hand.
    /// </summary>
    private static WorkflowModel Workflow(
        string fileName,
        string? description = "A workflow.",
        params ActivityModel[] roots) {
        var flat = new List<ActivityModel>();

        // Pre-order walk: assign the ordinal, append, then recurse — so Depth and Order
        // both come out the way XamlWorkflowParser produces them.
        ActivityModel Locate(ActivityModel node, string? parentId, int depth) {
            var located = new ActivityModel {
                Id = node.Id,
                IdRef = node.IdRef,
                ParentId = parentId,
                DisplayName = node.DisplayName,
                Type = node.Type,
                Depth = depth,
                Order = flat.Count,
                Line = node.Line
            };
            flat.Add(located);

            foreach (var child in node.Children) {
                located.Children.Add(Locate(child, node.Id, depth + 1));
            }

            return located;
        }

        foreach (var root in roots) {
            Locate(root, parentId: null, depth: 0);
        }

        return new WorkflowModel {
            FileName = fileName,
            RelativePath = fileName,
            FilePath = "/p/" + fileName,
            Description = description,
            Activities = flat,
            ExceptionHandlers = [new ExceptionHandlerModel { WorkflowName = fileName }],
            LogMessages = [new LogMessageModel()]
        };
    }

    private static ActivityModel Activity(
        string id,
        string type,
        string displayName,
        params ActivityModel[] children) => new() {
            Id = id,
            Type = type,
            DisplayName = displayName,
            Children = [.. children]
        };

    /// <summary>Entry workflow with handling/logging/description plus a test workflow.</summary>
    private static UiPathProjectModel Project(params WorkflowModel[] workflows) {
        var list = workflows.ToList();
        list.Insert(0, Workflow("Main.xaml", "Entry point.",
            Activity("sequence.1", "Sequence", "Main Sequence",
                Activity("sequence.1/logmessage.1", "LogMessage", "Log start"))));
        list.Insert(1, Workflow("Tests/TestMain.xaml", "Tests."));
        return new UiPathProjectModel {
            ProjectPath = "/p",
            ProjectName = "p",
            MainWorkflow = "Main.xaml",
            Workflows = list
        };
    }

    private static UiPathProjectModel WithOutputType(UiPathProjectModel model, string? outputType) => new() {
        ProjectPath = model.ProjectPath,
        ProjectName = model.ProjectName,
        MainWorkflow = model.MainWorkflow,
        OutputType = outputType,
        Workflows = model.Workflows,
        CodedWorkflows = model.CodedWorkflows
    };

    /// <summary>A REFramework state file: base <c>FileName</c>, folder-qualified <c>RelativePath</c>.</summary>
    private static WorkflowModel FrameworkFile(string baseName, string description) {
        var workflow = Workflow(baseName, description);
        workflow.RelativePath = "Framework/" + baseName;
        workflow.FilePath = "/p/Framework/" + baseName;
        return workflow;
    }

    private static Gap GapFor(List<Gap> gaps, string id) =>
        Assert.Single(gaps, g => g.Id == id);

    // ---------------------------------------------------------------- DisplayName

    [Theory]
    [InlineData("Sequence", "", "empty DisplayName")]
    [InlineData("Sequence", "   ", "whitespace DisplayName")]
    [InlineData("ReadRangeX", "ReadRangeX", "type-name default")]
    [InlineData("ReadRangeX", "Read Range X", "spaced type-name default")]
    [InlineData("Sequence", "Sequence1", "auto-numbered default")]
    [InlineData("NClick", "Click", "uix default without the N prefix")]
    [InlineData("LogMessage", "TODO name it", "placeholder marker")]
    public void Analyze_GenericDisplayName_ReportsLowConfidenceHint(string type, string displayName, string because) {
        var model = Project(Workflow("Steps.xaml", "Steps.", Activity("sequence.1/assign.1", type, displayName)));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "generic-displayname:Steps.xaml");

        Assert.Equal(Gap.Info, gap.Severity);
        // The type-name default is compared against a namespace-blind local name.
        Assert.Equal(Gap.ConfidenceLow, gap.Confidence);
        Assert.Equal(Gap.CategoryReadability, gap.Category);
        Assert.Equal("Steps.xaml", gap.TargetFile);
        Assert.Equal("edit_workflow_file", gap.SuggestedTool);
        Assert.Contains("Hint", gap.Message, StringComparison.Ordinal);
        Assert.True(displayName.Trim().Length == 0 || gap.Message.Contains(displayName, StringComparison.Ordinal), because);
    }

    [Theory]
    [InlineData("Read invoices from the ledger")]
    [InlineData("Click Submit on the SAP screen")]
    [InlineData("NClick")]
    [InlineData("Assign invoice total")]
    public void Analyze_MeaningfulDisplayName_ReportsNoGap(string displayName) {
        var model = Project(Workflow("Steps.xaml", "Steps.",
            Activity("sequence.1/assign.1", "Assign", displayName)));

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Id.StartsWith("generic-displayname:", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_GenericDisplayName_SummarizesAndCapsOffenders() {
        var children = Enumerable.Range(1, 6)
            .Select(i => Activity($"sequence.1/assign.{i}", "Assign", ""))
            .ToArray();
        var model = Project(Workflow("Steps.xaml", "Steps.",
            Activity("sequence.1", "Sequence", "Body", children: children)));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "generic-displayname:Steps.xaml");

        Assert.Contains("6 activity(ies)", gap.Message, StringComparison.Ordinal);
        Assert.Contains("+3 more", gap.Message, StringComparison.Ordinal);
        // Offenders are named by structural activity id, so the agent can find_activity them.
        Assert.Contains("sequence.1/assign.1", gap.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------- Argument prefixes

    [Fact]
    public void Analyze_ArgumentWithoutDirectionalPrefix_ReportsHighConfidenceInfo() {
        var workflow = Workflow("ProcessInvoice.xaml", "Processes one invoice.",
            Activity("sequence.1", "Sequence", "Body"));
        workflow.Arguments.Add(new ArgumentModel { Name = "InvoiceId", Direction = "In", Type = "String" });
        workflow.Arguments.Add(new ArgumentModel { Name = "Total", Direction = "Out", Type = "Decimal" });
        workflow.Arguments.Add(new ArgumentModel { Name = "Browser", Direction = "In/Out", Type = "Object" });

        var gap = GapFor(ProjectGapAnalyzer.Analyze(Project(workflow)), "argument-prefix-convention:ProcessInvoice.xaml");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Equal(Gap.CategoryReadability, gap.Category);
        Assert.Equal("manage_workflow_data", gap.SuggestedTool);
        Assert.Contains("in_InvoiceId", gap.Message, StringComparison.Ordinal);
        Assert.Contains("out_Total", gap.Message, StringComparison.Ordinal);
        Assert.Contains("io_Browser", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_PrefixedArguments_ReportsNoGap() {
        var workflow = Workflow("ProcessInvoice.xaml", "Processes one invoice.");
        workflow.Arguments.Add(new ArgumentModel { Name = "in_InvoiceId", Direction = "In", Type = "String" });
        workflow.Arguments.Add(new ArgumentModel { Name = "out_Total", Direction = "Out", Type = "Decimal" });
        workflow.Arguments.Add(new ArgumentModel { Name = "io_Browser", Direction = "In/Out", Type = "Object" });

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(Project(workflow)),
            g => g.Id.StartsWith("argument-prefix-convention:", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_LibraryProject_SkipsArgumentPrefixRule() {
        // Library public arguments drop the prefixes on purpose: they become activity
        // property names on the consumer's property grid.
        var workflow = Workflow("SendNotification.xaml", "Public library workflow.");
        workflow.Arguments.Add(new ArgumentModel { Name = "RecipientEmail", Direction = "In", Type = "String" });

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(WithOutputType(Project(workflow), "Library")),
            g => g.Id.StartsWith("argument-prefix-convention:", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_CodedEntryParameter_IsNotSubjectToPrefixRule() {
        // Coded .cs entry parameters are camelCase C# parameters; the directional prefixes
        // belong to the XAML argument surface that InvokeWorkflowFile binds.
        var model = Project();
        model.CodedWorkflows.Add(new CodedWorkflowModel {
            FileName = "ProcessCsv.cs",
            ClassName = "ProcessCsv",
            Kind = CodedFileKind.Workflow,
            IsCodedWorkflow = true,
            EntryMethods = ["Execute"],
            EntryHasTryCatch = true,
            EntryHasLog = true,
            EntryArguments = [new ArgumentModel { Name = "inputPath", Direction = "In", Type = "string" }]
        });

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Id.StartsWith("argument-prefix-convention:", StringComparison.Ordinal));
    }

    // ----------------------------------------------------------- Complexity metrics

    [Fact]
    public void Analyze_ExcessiveActivityCount_ReportsHighConfidenceInfo() {
        var children = Enumerable.Range(1, 31)
            .Select(i => Activity($"sequence.1/assign.{i}", "Assign", $"Set field {i}"))
            .ToArray();
        var model = Project(Workflow("Big.xaml", "Too much in one file.",
            Activity("sequence.1", "Sequence", "Body", children: children)));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "workflow-activity-count:Big.xaml");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Equal("add_xaml_workflow", gap.SuggestedTool);
        Assert.Contains("32 activities", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ExcessiveContainerActivityCount_ReportsPerContainer() {
        var children = Enumerable.Range(1, 13)
            .Select(i => Activity($"sequence.1/sequence.1/assign.{i}", "Assign", $"Set field {i}"))
            .ToArray();
        var model = Project(Workflow("Wide.xaml", "One wide container.",
            Activity("sequence.1", "Sequence", "Body",
                Activity("sequence.1/sequence.1", "Sequence", "Wide step", children: children))));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "container-activity-count:Wide.xaml:sequence.1/sequence.1");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Contains("13 direct", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ExcessiveNestingDepth_ReportsDeepestActivity() {
        // Nesting levels are Depth + 1 so the count matches what Studio's outline shows.
        // Build a chain nested to depth 7 (8 levels), deepest node last so Children nest.
        var deepest = Activity("level.8", "Assign", "Set total");
        for (var depth = 6; depth >= 1; depth--) {
            deepest = Activity($"level.{depth}", "Sequence", $"Level {depth}", deepest);
        }

        var model = Project(Workflow("Deep.xaml", "Deeply nested control flow.",
            Activity("level.0", "Sequence", "Body", deepest)));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "deep-container-nesting:Deep.xaml");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Contains("8 levels deep", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ModestWorkflow_ReportsNoComplexityGaps() {
        var model = Project(Workflow("Small.xaml", "One step.",
            Activity("sequence.1", "Sequence", "Body",
                Activity("sequence.1/assign.1", "Assign", "Set total"))));

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("workflow-activity-count:", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("container-activity-count:", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("deep-container-nesting:", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- Rule 24 wraps

    [Fact]
    public void Analyze_SingleBodyContainerWithMultipleChildren_ReportsUnwrappedBody() {
        var model = Project(Workflow("Loop.xaml", "Retry the step.",
            Activity("sequence.1/retryscope.1", "RetryScope", "Retry submit",
                Activity("sequence.1/retryscope.1/logmessage.1", "LogMessage", "Log attempt"),
                Activity("sequence.1/retryscope.1/assign.1", "Assign", "Set attempt"))));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model),
            "unwrapped-container-body:Loop.xaml:sequence.1/retryscope.1");

        Assert.Equal(Gap.Warning, gap.Severity);
        // The child count is structural; only the container type is name-matched.
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
        Assert.Equal(Gap.CategoryReadability, gap.Category);
        Assert.Contains("2 direct", gap.Message, StringComparison.Ordinal);
        Assert.Contains("Rule 24", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_SingleBodyContainerWithBareChild_ReportsLowConfidenceHint() {
        var model = Project(Workflow("Loop.xaml", "Wait for the file.",
            Activity("sequence.1/while.1", "While", "Wait loop",
                Activity("sequence.1/while.1/delay.1", "Delay", "Wait a second"))));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model),
            "container-body-not-sequence:Loop.xaml:sequence.1/while.1");

        Assert.Equal(Gap.Info, gap.Severity);
        // Bare-body detection compares a namespace-blind type name: hint only.
        Assert.Equal(Gap.ConfidenceLow, gap.Confidence);
        Assert.Contains("Hint", gap.Message, StringComparison.Ordinal);
        Assert.Contains("validate and build both accept the bare form", gap.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("While")]
    [InlineData("DoWhile")]
    [InlineData("InterruptibleWhile")]
    [InlineData("InterruptibleDoWhile")]
    [InlineData("RetryScope")]
    public void Analyze_WrappedSingleBodyContainer_ReportsNoGap(string containerType) {
        var model = Project(Workflow("Loop.xaml", "Wrapped body.",
            Activity("sequence.1/loop.1", containerType, "Loop",
                Activity("sequence.1/loop.1/sequence.1", "Sequence", "Body",
                    Activity("sequence.1/loop.1/sequence.1/assign.1", "Assign", "Set flag")))));

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("unwrapped-container-body:", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith("container-body-not-sequence:", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_SlotNamedContainerBody_IsNotReported_NeedsParserSupport() {
        // XamlActivityLocator makes attached-property slots transparent and does not
        // consume depth for them, so a bare If.Then and a Sequence-wrapped one are
        // identical in the parsed model. The rule deliberately does not fire.
        var model = Project(Workflow("Branch.xaml", "Branch on the total.",
            Activity("sequence.1/if.1", "If", "If over limit",
                Activity("sequence.1/if.1/assign.1", "Assign", "Set flag"))));

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Category == Gap.CategoryReadability && g.Id.Contains("container-body", StringComparison.Ordinal));
    }

    // ------------------------------------------------------- Description coverage

    [Fact]
    public void Analyze_MostlyUndocumentedProject_ReportsCoverageGap() {
        // Main + Tests/TestMain (both described) + four undocumented = 2 of 6.
        var model = Project(
            Workflow("A.xaml", null),
            Workflow("B.xaml", null),
            Workflow("C.xaml", null),
            Workflow("D.xaml", null));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "description-coverage-low");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Equal(Gap.CategoryReadability, gap.Category);
        Assert.Contains("2 of 6 workflows", gap.Message, StringComparison.Ordinal);
        Assert.Contains("Annotation.AnnotationText", gap.SuggestedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_MajorityDocumentedProject_ReportsNoCoverageGap() {
        var model = Project(
            Workflow("A.xaml", "Does A."),
            Workflow("B.xaml", "Does B."),
            Workflow("C.xaml", null));

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model), g => g.Id == "description-coverage-low");
    }

    [Fact]
    public void Analyze_TinyProject_ReportsNoCoverageGap() {
        // Main + Tests/TestMain only: below the project-advice floor.
        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(Project()), g => g.Id == "description-coverage-low");
    }

    // --------------------------------------------------- REFramework conformance

    private static UiPathProjectModel ReFrameworkProject(params WorkflowModel[] extra) {
        var init = FrameworkFile("InitAllSettings.xaml", "Reads Config.xlsx.");
        var get = FrameworkFile("GetTransactionData.xaml", "Returns the next item.");
        var process = FrameworkFile("Process.xaml", "Business logic.");
        var set = FrameworkFile("SetTransactionStatus.xaml", "Updates queue status.");
        get.Arguments.Add(new ArgumentModel { Name = "out_TransactionItem", Direction = "Out", Type = "QueueItem" });
        set.Arguments.Add(new ArgumentModel { Name = "in_TransactionItem", Direction = "In", Type = "QueueItem" });
        return Project([init, get, process, set, .. extra]);
    }

    [Fact]
    public void Analyze_ConformantReFramework_ReportsNoConformanceGaps() {
        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(ReFrameworkProject()),
            g => g.Id.StartsWith("reframework-", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ReFrameworkMissingStateFile_ReportsWarning() {
        var model = ReFrameworkProject();
        model.Workflows.RemoveAll(w => w.FileName == "SetTransactionStatus.xaml");

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "reframework-missing-file:SetTransactionStatus.xaml");

        Assert.Equal(Gap.Warning, gap.Severity);
        // Classification is by canonical file name, not by parsed state-machine shape.
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
        Assert.Equal(Gap.CategoryReadability, gap.Category);
        Assert.Equal("add_xaml_workflow", gap.SuggestedTool);
        Assert.Contains("InitAllSettings.xaml", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ReFrameworkWithoutTransactionItemArguments_ReportsBothWiringGaps() {
        var gaps = ProjectGapAnalyzer.Analyze(Project(
            FrameworkFile("InitAllSettings.xaml", "Reads Config.xlsx."),
            FrameworkFile("GetTransactionData.xaml", "Returns the next item."),
            FrameworkFile("Process.xaml", "Business logic."),
            FrameworkFile("SetTransactionStatus.xaml", "Updates queue status.")));

        var get = GapFor(gaps, "reframework-no-get-transaction-item");
        Assert.Equal(Gap.Warning, get.Severity);
        // Parsed x:Property members: the argument surface is structural.
        Assert.Equal(Gap.ConfidenceHigh, get.Confidence);
        Assert.Equal("manage_workflow_data", get.SuggestedTool);
        Assert.Contains("out_TransactionItem", get.Message, StringComparison.Ordinal);

        var set = GapFor(gaps, "reframework-no-set-transaction-status");
        Assert.Equal(Gap.Warning, set.Severity);
        Assert.Equal(Gap.ConfidenceHigh, set.Confidence);
        Assert.Contains("in_TransactionItem", set.Message, StringComparison.Ordinal);
        Assert.Contains("BusinessRuleException", set.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ReFrameworkWithGlobalHandler_ReportsConflict() {
        var model = ReFrameworkProject(Workflow("GlobalHandlerX.xaml", "Last-line handler."));

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "reframework-global-handler-conflict:GlobalHandlerX.xaml");

        Assert.Equal(Gap.Warning, gap.Severity);
        // File-name detection only; project.json runtimeOptions is not in the model.
        Assert.Equal(Gap.ConfidenceLow, gap.Confidence);
        Assert.Equal("patch_project_json", gap.SuggestedTool);
        Assert.Contains("pick one strategy", gap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hint", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_NonReFrameworkProject_DoesNotDemandFrameworkFiles() {
        var model = Project(
            Workflow("ReadInvoices.xaml", "Reads invoices."),
            Workflow("PostToErp.xaml", "Posts to ERP."));

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Id.StartsWith("reframework-", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_ProcessProjectWithoutGlobalHandler_AdvisesOne() {
        var model = WithOutputType(Project(
            Workflow("ReadInvoices.xaml", "Reads invoices."),
            Workflow("PostToErp.xaml", "Posts to ERP.")), "Process");

        var gap = GapFor(ProjectGapAnalyzer.Analyze(model), "no-global-handler");

        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
        Assert.Equal("add_xaml_workflow", gap.SuggestedTool);
        Assert.Contains("errorInfo", gap.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains("Do not add one if the project later adopts REFramework", gap.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Library")]
    public void Analyze_NonProcessProjectWithoutGlobalHandler_DoesNotAdvise(string? outputType) {
        var model = WithOutputType(Project(
            Workflow("ReadInvoices.xaml", "Reads invoices."),
            Workflow("PostToErp.xaml", "Posts to ERP.")), outputType);

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model), g => g.Id == "no-global-handler");
    }

    [Fact]
    public void Analyze_ProjectAlreadyHavingGlobalHandler_DoesNotAdviseAnother() {
        var model = WithOutputType(Project(
            Workflow("GlobalHandlerX.xaml", "Last-line handler."),
            Workflow("ReadInvoices.xaml", "Reads invoices."),
            Workflow("PostToErp.xaml", "Posts to ERP.")), "Process");

        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model), g => g.Id == "no-global-handler");
    }

    // ------------------------------------------------------------------- Grading

    [Fact]
    public void Analyze_ReadabilityGaps_NeverOutrankBlockingCategories() {
        // Readability is advisory: nothing in it may be error severity, or the Copilot loop
        // would be forced to edit files purely for style.
        var model = ReFrameworkProject(
            Workflow("Steps.xaml", null, Activity("sequence.1", "Sequence", "Sequence")),
            Workflow("GlobalHandlerX.xaml", null));

        var gaps = ProjectGapAnalyzer.Analyze(model);

        Assert.Contains(gaps, g => g.Category == Gap.CategoryReadability);
        Assert.All(gaps.Where(g => g.Category == Gap.CategoryReadability), g => {
            Assert.NotEqual(Gap.Error, g.Severity);
            Assert.False(string.IsNullOrWhiteSpace(g.Message), g.Id);
            // Every gap must name a real fix: either a tool or an explicit action.
            Assert.True(!string.IsNullOrWhiteSpace(g.SuggestedTool) || !string.IsNullOrWhiteSpace(g.SuggestedAction), g.Id);
        });
    }

    [Fact]
    public void Analyze_SuggestedTools_AreRealCopilotSurfaceTools() {
        // Assert against the shipped connector catalog so the test cannot drift when a
        // tool is renamed or moved leave-off.
        var known = new HashSet<string>(
            CopilotConnectorTools.DefaultNames.Concat(CopilotConnectorTools.LeaveOffNames),
            StringComparer.Ordinal);
        var model = ReFrameworkProject(
            Workflow("Steps.xaml", null, Activity("sequence.1", "Sequence", "Sequence")),
            Workflow("GlobalHandlerX.xaml", null));

        foreach (var gap in ProjectGapAnalyzer.Analyze(model).Where(g => g.SuggestedTool is not null)) {
            Assert.Contains(gap.SuggestedTool!, known);
        }
    }
}
