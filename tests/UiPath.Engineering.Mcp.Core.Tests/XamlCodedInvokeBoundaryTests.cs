using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class XamlCodedInvokeBoundaryTests {
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

    private static CodedWorkflowModel CodedWorkflow(params ArgumentModel[] arguments) => new() {
        FileName = "InvoiceFlow.cs",
        ClassName = "InvoiceFlow",
        Kind = CodedFileKind.Workflow,
        IsCodedWorkflow = true,
        EntryArguments = [.. arguments]
    };

    private static CodedWorkflowModel SourceClass(string className, string ns = "", params string[] publicMethods) => new() {
        FileName = className + ".cs",
        ClassName = className,
        Namespace = ns,
        Kind = CodedFileKind.Source,
        PublicMethods = [.. publicMethods]
    };

    [Theory]
    [InlineData("x:String")]
    [InlineData("int")]
    [InlineData("System.Data.DataTable")]
    [InlineData("string[]")]
    [InlineData("x:Boolean")]
    [InlineData("Dictionary<string, object>")]
    [InlineData("IEnumerable<string>")]
    [InlineData("List<int>")]
    [InlineData("System.Data.DataRow")]
    [InlineData("object[]")]
    public void Lint_AllowedFrameworkTypes_ReportsNoBoundaryGaps(string type) {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Value", Type = type, Expression = "[value]"
            }),
            CodedWorkflow(new ArgumentModel { Name = "in_Value", Direction = "In", Type = "string" }));

        var gaps = XamlCodedInvokeBoundary.Lint(model);

        Assert.Empty(gaps);
        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model), g => g.Category == "boundary");
    }

    [Fact]
    public void Lint_CustomClassArgument_ReportsNonPrimitiveError() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Customer", Type = "local:CustomerRecord", Expression = "[customer]"
            }),
            CodedWorkflow());

        var gaps = XamlCodedInvokeBoundary.Lint(model);

        var gap = Assert.Single(gaps);
        Assert.StartsWith(XamlCodedInvokeBoundary.NonPrimitiveIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal("boundary", gap.Category);
        Assert.Contains("CustomerRecord", gap.Message);
        Assert.Contains("in_Customer", gap.Message);
    }

    [Fact]
    public void Lint_EnumerableOfSourceClass_ReportsProjectTypeError() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Customers", Type = "IEnumerable<CustomerRecord>", Expression = "[customers]"
            }),
            CodedWorkflow(),
            SourceClass("CustomerRecord"));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.StartsWith(XamlCodedInvokeBoundary.NonPrimitiveIdPrefix, gap.Id);
        Assert.Contains("IEnumerable<CustomerRecord>", gap.Message);
        Assert.Contains("in_Customers", gap.Message);
    }

    [Fact]
    public void Lint_CustomClassOnCodedExecute_ReportsWhenXamlOmitsType() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Customer", Expression = "[customer]"
            }),
            new CodedWorkflowModel {
                FileName = "InvoiceFlow.cs",
                ClassName = "InvoiceFlow",
                Kind = CodedFileKind.Workflow,
                IsCodedWorkflow = true,
                EntryArguments = [new ArgumentModel { Name = "in_Customer", Type = "CustomerRecord", Direction = "In" }]
            },
            SourceClass("CustomerRecord"));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.Contains("CustomerRecord", gap.Message);
    }

    [Fact]
    public void Lint_SourceMethodFromXamlExpression_ReportsError() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Name", Type = "x:String", Expression = "[Helpers.Format(id)]"
            }),
            CodedWorkflow(),
            SourceClass("Helpers", publicMethods: ["Format"]));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.StartsWith(XamlCodedInvokeBoundary.SourceMethodIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        // An argument binding is always expression text, so a call token there is a call site.
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
        Assert.Contains("Helpers.Format", gap.Message);
        Assert.Equal("add_coded_workflow", gap.SuggestedTool);
    }

    [Fact]
    public void Lint_LogMessageProseMentioningSourceMethod_DoesNotReportBoundaryViolation() {
        // The message is free text. "Call Helpers.Format" is prose, not a call, and used to
        // raise an error-severity violation the Copilot loop had to remediate.
        var main = MainInvoking("InvoiceFlow.cs");
        main.LogMessages.Add(new LogMessageModel {
            DisplayName = "Log hint",
            Level = "Info",
            Message = "Call Helpers.Format on the invoice id before posting."
        });
        var model = Project(main, CodedWorkflow(), SourceClass("Helpers", publicMethods: ["Format"]));

        var gaps = XamlCodedInvokeBoundary.Lint(model);

        Assert.DoesNotContain(gaps, g => g.Id.StartsWith(XamlCodedInvokeBoundary.SourceMethodIdPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(ProjectGapAnalyzer.Analyze(model),
            g => g.Category == "boundary" && g.Severity == Gap.Error);
    }

    [Fact]
    public void Lint_LogMessageWithCallShapedToken_ReportsLowConfidenceHint() {
        var main = MainInvoking("InvoiceFlow.cs");
        main.LogMessages.Add(new LogMessageModel {
            DisplayName = "Log hint",
            Level = "Info",
            Message = "Failed at Helpers.Format(id) for invoice 42."
        });
        var model = Project(main, CodedWorkflow(), SourceClass("Helpers", publicMethods: ["Format"]));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model),
            g => g.Id.StartsWith(XamlCodedInvokeBoundary.SourceMethodIdPrefix, StringComparison.Ordinal));

        // Downgraded from error: raw attribute text cannot distinguish a call from prose.
        Assert.Equal(Gap.Info, gap.Severity);
        Assert.Equal(Gap.ConfidenceLow, gap.Confidence);
        Assert.Contains("Hint", gap.Message, StringComparison.Ordinal);
        Assert.Contains("verify", gap.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("edit_workflow_file", gap.SuggestedTool);
    }

    [Fact]
    public void Lint_ExpressionFindingIsNotDowngradedByProseMention() {
        // Same member found in an argument binding (a real call) and in prose: the
        // error-severity finding must win, because both share one gap id.
        var main = MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
            Direction = "In", TargetArgument = "in_Name", Type = "x:String", Expression = "[Helpers.Format(id)]"
        });
        main.LogMessages.Add(new LogMessageModel {
            DisplayName = "Log hint",
            Level = "Info",
            Message = "Failed at Helpers.Format(id)."
        });
        var model = Project(main, CodedWorkflow(), SourceClass("Helpers", publicMethods: ["Format"]));

        var gaps = XamlCodedInvokeBoundary.Lint(model);

        var gap = Assert.Single(gaps,
            g => g.Id.StartsWith(XamlCodedInvokeBoundary.SourceMethodIdPrefix, StringComparison.Ordinal));
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
    }

    [Fact]
    public void Lint_ExplicitLocalPrefixArgument_IsHighConfidence() {
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Customer", Type = "local:CustomerRecord", Expression = "[customer]"
            }),
            CodedWorkflow(),
            SourceClass("CustomerRecord"));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.StartsWith(XamlCodedInvokeBoundary.NonPrimitiveIdPrefix, gap.Id);
        Assert.Equal(Gap.Error, gap.Severity);
        // An explicit local: prefix is certain, unlike a bare simple name.
        Assert.Equal(Gap.ConfidenceHigh, gap.Confidence);
    }

    [Fact]
    public void Lint_BareSimpleNameArgument_IsMediumConfidence() {
        // Namespace-blind matching: a NuGet type sharing the project class's simple name
        // is indistinguishable, so this stays a medium-confidence finding.
        var model = Project(
            MainInvoking("InvoiceFlow.cs", new ArgumentMappingModel {
                Direction = "In", TargetArgument = "in_Customer", Type = "CustomerRecord", Expression = "[customer]"
            }),
            CodedWorkflow(),
            SourceClass("CustomerRecord"));

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.Equal(Gap.Error, gap.Severity);
        Assert.Equal(Gap.ConfidenceMedium, gap.Confidence);
    }

    [Fact]
    public void Lint_InvokeOfCodedSourceFile_ReportsError() {
        var model = Project(
            MainInvoking("Helpers.cs"),
            new CodedWorkflowModel {
                FileName = "Helpers.cs",
                ClassName = "Helpers",
                Kind = CodedFileKind.Source,
                PublicMethods = ["Format"]
            });

        var gap = Assert.Single(XamlCodedInvokeBoundary.Lint(model));
        Assert.StartsWith(XamlCodedInvokeBoundary.SourceInvokeIdPrefix, gap.Id);
        Assert.Contains("Helpers.cs", gap.Message);
    }

    [Theory]
    [InlineData("x:String")]
    [InlineData("string[]")]
    [InlineData("DataTable")]
    [InlineData("int?")]
    [InlineData("int[]")]
    [InlineData("Dictionary<string, object>")]
    [InlineData("IEnumerable<string>")]
    [InlineData("List<int>")]
    [InlineData("System.Data.DataRow")]
    [InlineData("object[]")]
    [InlineData("object")]
    [InlineData("IDictionary")]
    [InlineData("IEnumerable")]
    [InlineData("scg:Dictionary(x:String, x:Object)")]
    [InlineData("List(Of String)")]
    public void IsAllowedArgumentType_AllowsFrameworkTypes(string type) {
        Assert.True(XamlCodedInvokeBoundary.IsAllowedArgumentType(type));
    }

    [Theory]
    [InlineData("local:CustomerRecord")]
    [InlineData("IEnumerable<CustomerRecord>")]
    [InlineData("IEnumerable<local:CustomerRecord>")]
    [InlineData("List<CustomerRecord>")]
    [InlineData("scg:List(local:CustomerRecord)")]
    public void IsAllowedArgumentType_RejectsProjectDefinedTypes(string type) {
        Assert.False(XamlCodedInvokeBoundary.IsAllowedArgumentType(type, [SourceClass("CustomerRecord")]));
    }

    [Fact]
    public void IsAllowedArgumentType_RejectsTypeInProjectNamespace() {
        var coded = SourceClass("CustomerRecord", "MyApp.Dto");

        Assert.False(XamlCodedInvokeBoundary.IsAllowedArgumentType("MyApp.Dto.OrderLine", [coded]));
        Assert.True(XamlCodedInvokeBoundary.IsAllowedArgumentType("System.Data.DataRow", [coded]));
    }
}
