using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CodedExecuteArgumentGapsTests {
    private static WorkflowModel MainInvoking(string target, params ArgumentMappingModel[] mappings) => new() {
        FileName = "Main.xaml",
        IsMain = true,
        Description = "Entry.",
        ExceptionHandlers = [new ExceptionHandlerModel { WorkflowName = "Main.xaml" }],
        LogMessages = [new LogMessageModel()],
        InvokeWorkflows = [
            new InvokeWorkflowModel {
                SourceWorkflow = "Main.xaml",
                TargetWorkflow = target,
                ArgumentMappings = [.. mappings]
            }
        ]
    };

    private static UiPathProjectModel Project(WorkflowModel main, params CodedWorkflowModel[] coded) => new() {
        ProjectName = "p",
        MainWorkflow = "Main.xaml",
        Workflows = [main, new WorkflowModel { FileName = "Tests/TestMain.xaml", Description = "Tests." }],
        CodedWorkflows = [.. coded]
    };

    private static CodedWorkflowModel Workflow(params ArgumentModel[] arguments) => new() {
        FileName = "InvoiceFlow.cs",
        ClassName = "InvoiceFlow",
        Kind = CodedFileKind.Workflow,
        IsCodedWorkflow = true,
        EntryMethods = ["Execute"],
        EntryArguments = [.. arguments]
    };

    private static ArgumentModel Arg(string name, bool hasDefault = false) => new() {
        Name = name,
        Direction = "In",
        Type = "string",
        HasDefault = hasDefault
    };

    private static ArgumentMappingModel Bind(string name) => new() {
        Direction = "In",
        TargetArgument = name,
        Type = "x:String",
        Expression = "[value]"
    };

    [Fact]
    public void Lint_MatchingNames_ReportsNoArgumentGaps() {
        var model = Project(
            MainInvoking("Workflows/InvoiceFlow.cs", Bind("in_InvoiceId"), Bind("IN_TOTAL")),
            Workflow(Arg("in_InvoiceId"), Arg("in_Total")));

        Assert.Empty(CodedExecuteArgumentGaps.Lint(model));
        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Id.StartsWith("coded-invoke-arg-", StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_MissingExecuteParameter_PointsAtInsertActivities() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_InvoiceId")),
            Workflow(Arg("in_InvoiceId"), Arg("in_Total")));

        var gap = Assert.Single(CodedExecuteArgumentGaps.Lint(model));

        Assert.StartsWith(CodedExecuteArgumentGaps.MissingIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Equal("boundary", gap.Category);
        Assert.Equal("Main.xaml", gap.TargetFile);
        Assert.Contains("missing", gap.Message, StringComparison.Ordinal);
        Assert.Contains("in_Total", gap.Message, StringComparison.Ordinal);
        Assert.Equal("insert_activities", gap.SuggestedTool);
        Assert.Contains("insert_activities", gap.SuggestedAction, StringComparison.Ordinal);
        Assert.Contains(ProjectGapAnalyzer.Analyze(model), g => g.Id == gap.Id);
    }

    [Fact]
    public void Lint_ExtraBinding_PointsAtInsertActivities() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_InvoiceId"), Bind("in_Legacy")),
            Workflow(Arg("in_InvoiceId")));

        var gap = Assert.Single(CodedExecuteArgumentGaps.Lint(model));

        Assert.StartsWith(CodedExecuteArgumentGaps.ExtraIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Contains("extra", gap.Message, StringComparison.Ordinal);
        Assert.Contains("in_Legacy", gap.Message, StringComparison.Ordinal);
        Assert.Equal("insert_activities", gap.SuggestedTool);
        Assert.Contains("insert_activities", gap.SuggestedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_RenamedParameter_PointsAtManageWorkflowData() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_InvoiceId")),
            Workflow(Arg("in_InvoiceNumber")));

        var gap = Assert.Single(CodedExecuteArgumentGaps.Lint(model));

        Assert.StartsWith(CodedExecuteArgumentGaps.RenamedIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
        Assert.Contains("renamed", gap.Message, StringComparison.Ordinal);
        Assert.Contains("in_InvoiceId", gap.Message, StringComparison.Ordinal);
        Assert.Contains("in_InvoiceNumber", gap.Message, StringComparison.Ordinal);
        Assert.Equal("manage_workflow_data", gap.SuggestedTool);
        Assert.Contains("manage_workflow_data", gap.SuggestedAction, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_PrefixOnlyNameChange_ReportsRenamed() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_Amount")),
            Workflow(Arg("amount")));

        var gap = Assert.Single(CodedExecuteArgumentGaps.Lint(model));

        Assert.StartsWith(CodedExecuteArgumentGaps.RenamedIdPrefix, gap.Id);
        Assert.Contains("amount", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lint_DissimilarMissingAndExtra_AreNotARename() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("customerName")),
            Workflow(Arg("in_Total")));

        var gaps = CodedExecuteArgumentGaps.Lint(model);

        Assert.Equal(2, gaps.Count);
        Assert.Contains(gaps, g => g.Id.StartsWith(CodedExecuteArgumentGaps.MissingIdPrefix, StringComparison.Ordinal)
            && g.Message.Contains("in_Total", StringComparison.Ordinal));
        Assert.Contains(gaps, g => g.Id.StartsWith(CodedExecuteArgumentGaps.ExtraIdPrefix, StringComparison.Ordinal)
            && g.Message.Contains("customerName", StringComparison.Ordinal));
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith(CodedExecuteArgumentGaps.RenamedIdPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_AmbiguousRename_StaysMissingAndExtra() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_InvoiceNumber"), Bind("in_InvoiceCode")),
            Workflow(Arg("in_InvoiceId")));

        var gaps = CodedExecuteArgumentGaps.Lint(model);

        Assert.Equal(3, gaps.Count);
        Assert.Contains(gaps, g => g.Id.StartsWith(CodedExecuteArgumentGaps.MissingIdPrefix, StringComparison.Ordinal));
        Assert.Equal(2, gaps.Count(g => g.Id.StartsWith(CodedExecuteArgumentGaps.ExtraIdPrefix, StringComparison.Ordinal)));
        Assert.DoesNotContain(gaps, g => g.Id.StartsWith(CodedExecuteArgumentGaps.RenamedIdPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Lint_UnboundOptionalParameter_IsNotMissing() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", Bind("in_InvoiceId")),
            Workflow(Arg("in_InvoiceId"), Arg("in_Note", hasDefault: true)));

        Assert.Empty(CodedExecuteArgumentGaps.Lint(model));
    }

    [Fact]
    public void Lint_XamlTargetAndCodedSource_AreIgnored() {
        var xamlInvoke = Project(
            MainInvoking("Child.xaml", Bind("in_Other")),
            Workflow(Arg("in_InvoiceId")));
        var sourceInvoke = Project(
            MainInvoking("Helpers.cs", Bind("in_Other")),
            new CodedWorkflowModel {
                FileName = "Helpers.cs",
                ClassName = "Helpers",
                Kind = CodedFileKind.Source,
                EntryArguments = [Arg("in_InvoiceId")]
            });

        Assert.Empty(CodedExecuteArgumentGaps.Lint(xamlInvoke));
        Assert.Empty(CodedExecuteArgumentGaps.Lint(sourceInvoke));
    }
}
