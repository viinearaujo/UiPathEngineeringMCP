using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.CodeSearch;
using UiPath.Engineering.Mcp.Core.GapAnalysis;
using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

internal sealed record EvalOutcome(
    string Id,
    string Name,
    bool Passed,
    bool? SpecValidates,
    bool? XamlEmits,
    bool? UnknownXamlWriteSucceeded,
    string Detail) {
    /// <summary>Which structural check families this eval exercised, for the recorded scorecard.</summary>
    public IReadOnlyList<string> Families { get; init; } = [];
}

internal sealed class GoldenEvalContext {
    public const string ProjectPath = "/projects/evalProcess";

    public FakeFilesystemProvider Fs { get; }
    public FakeUiPathCliProvider Cli { get; }
    public FakeActivityDiscovery Discovery { get; }
    public ValidateActivitySpecTool ValidateSpec { get; }
    public BuildWorkflowTool Build { get; }
    public WriteWorkflowFileTool Write { get; }
    public RecommendActivitiesTool Recommend { get; }
    public ValidateProjectTool ValidateProject { get; }
    public BackgroundJobStore Jobs { get; }

    /// <summary>
    /// <paramref name="projectModelBuilder"/> decides the expression language the
    /// authoring tools read from the project. It is injected at construction, not
    /// set afterwards, because the tools capture it in their own constructors:
    /// pass a <see cref="FakeProjectModelBuilder"/> with
    /// <c>ExpressionLanguage = "CSharp"</c> to render a spec as a C# project. A
    /// null builder leaves the tools on <c>ProjectXamlSettings.Default</c>
    /// (VisualBasic), which is the historical behavior.
    /// </summary>
    public GoldenEvalContext(IProjectModelBuilder? projectModelBuilder = null) {
        Fs = new FakeFilesystemProvider {
            ProjectJson = "/projects/evalProcess/project.json",
            ProjectJsonContent = """
                {
                  "name": "EvalProcess",
                  "main": "Main.xaml",
                  "dependencies": {
                    "UiPath.System.Activities": "[24.10.0]",
                    "UiPath.Excel.Activities": "[2.24.0]"
                  }
                }
                """
        };
        Discovery = new FakeActivityDiscovery {
            Hits = [
                new DiscoveredActivity(
                    "ReadRangeX",
                    "UiPath.Excel.Activities.Business.ReadRangeX",
                    "UiPath.Excel.Activities",
                    "2.24.0")
            ]
        };
        Cli = new FakeUiPathCliProvider();
        var resolver = TestCatalogs.Resolver(Fs, Discovery);
        ValidateSpec = new ValidateActivitySpecTool(resolver, projectModelBuilder);
        Build = new BuildWorkflowTool(Fs, resolver, projectModelBuilder);
        Write = new WriteWorkflowFileTool(Fs, resolver);
        Recommend = new RecommendActivitiesTool(Fs, resolver);
        Jobs = BackgroundJobs.NewStore();
        ValidateProject = new ValidateProjectTool(Cli, Fs, Jobs);
    }

    public static GoldenEvalContext CSharp() =>
        new(new FakeProjectModelBuilder {
            Model = new UiPathProjectModel { ProjectName = "EvalProcess", ExpressionLanguage = "CSharp" }
        });

    public static GoldenEvalContext VisualBasic() =>
        new(new FakeProjectModelBuilder {
            Model = new UiPathProjectModel { ProjectName = "EvalProcess", ExpressionLanguage = "VisualBasic" }
        });

    public static string LoadSpec(string fileName) {
        var path = Path.Combine(AppContext.BaseDirectory, "evals", "specs", fileName);
        if (!File.Exists(path)) {
            throw new FileNotFoundException(
                $"Golden spec '{fileName}' was not copied to the test output. Expected '{path}'.", path);
        }

        return File.ReadAllText(path);
    }

    public static JsonElement Data(object? data) =>
        JsonSerializer.SerializeToElement(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public static string Target(string relativePath) =>
        Path.Combine(Path.GetFullPath(ProjectPath), relativePath.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>
/// Spec JSON used by the structural evals. Held in code rather than as separate
/// files so the eval surface stays inside <c>tests/**/Evals/**</c>; the original
/// seven live on in <c>evals/specs/</c> and are still exercised.
/// </summary>
internal static class EvalSpecs {
    /// <summary>Assign generic form, annotation round-trip, and &lt;x:Members&gt; arguments.</summary>
    public const string AssignAnnotationArguments = """
        {
          "name": "Sequence",
          "annotation": "root note",
          "workflowArguments": [
            { "name": "in_Path", "type": "String", "direction": "In" },
            { "name": "out_Ok", "type": "Boolean", "direction": "Out" }
          ],
          "children": [
            {
              "name": "Assign",
              "annotation": "assign note",
              "properties": { "typeArgument": "Int32", "to": "[counter]", "value": "[counter + 1]" }
            }
          ]
        }
        """;

    /// <summary>
    /// Valid verbatim in both languages: a quoted string literal is an accepted
    /// expression form in VisualBasic and in CSharp, but it renders as an
    /// attribute in VB and as a typed CSharpValue property element in C#.
    /// </summary>
    public const string SharedLiteralMessage = """
        {
          "name": "Sequence",
          "children": [
            { "name": "LogMessage", "properties": { "message": "\"hello\"", "level": "Info" } }
          ]
        }
        """;

    /// <summary>Bracket shorthand, which a C# project must reject (deserializes as VisualBasicValue).</summary>
    public const string BracketShorthand = """
        {
          "name": "Sequence",
          "children": [
            { "name": "LogMessage", "properties": { "message": "[fileName]", "level": "Info" } }
          ]
        }
        """;

    /// <summary>The documented placeholder-selector UIA path: real uix activities, TODO Indicate markers.</summary>
    public const string UiaPlaceholderSelector = """
        {
          "name": "Sequence",
          "children": [
            {
              "name": "NApplicationCard",
              "properties": { "displayName": "TODO Indicate: app window" },
              "children": [
                {
                  "name": "NClick",
                  "properties": { "displayName": "TODO Indicate: click ok", "target": "" }
                }
              ]
            }
          ]
        }
        """;

    /// <summary>Flowchart with two wired nodes and a decision back-edge.</summary>
    public const string Flowchart = """
        {
          "name": "Flowchart",
          "flowchart": {
            "start": "1",
            "nodes": [
              {
                "id": "1", "type": "Step", "displayName": "First", "next": "2",
                "activity": { "name": "WriteLine", "properties": { "displayName": "log", "text": "\"hi\"" } }
              },
              {
                "id": "2", "type": "Decision", "displayName": "Branch?",
                "condition": "[flag]", "true": "1"
              }
            ]
          }
        }
        """;

    /// <summary>StateMachine with a two-state transition graph.</summary>
    public const string StateMachine = """
        {
          "name": "StateMachine",
          "stateMachine": {
            "start": "1",
            "states": [
              {
                "id": "1", "displayName": "Idle",
                "activities": [ { "name": "WriteLine", "properties": { "displayName": "log", "text": "\"hi\"" } } ],
                "transitions": [ { "displayName": "go", "to": "2", "condition": "[ready]" } ]
              },
              { "id": "2", "displayName": "Done", "kind": "FinalState" }
            ]
          }
        }
        """;

    /// <summary>A single-body container whose body is wrapped (Rule 24) and uses Studio's InterruptibleWhile.</summary>
    public const string WhileLoop = """
        {
          "name": "While",
          "properties": { "condition": "[go]", "displayName": "While" },
          "children": [
            { "name": "WriteLine", "properties": { "text": "\"x\"" } }
          ]
        }
        """;
}

internal static class GoldenEvalTasks {
    // A genuinely unknown activity, used to prove the catalog guard still refuses
    // XAML that is not in the catalog. This is the safety property that the UIA
    // eval (08) must not weaken: the guard is the reason the UIA path had to be
    // added to the catalog rather than bypassed.
    public const string UnknownActivityXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <ui:ThisActivityDoesNotExist DisplayName="Nope" />
        </Activity>
        """;

    public const string BrokenInvokeXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities"
                  xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
          <Sequence sap2010:WorkflowViewState.IdRef="Sequence_1">
            <ui:InvokeWorkflowFile DisplayName="Call child" WorkflowFileName="Missing.xaml" sap2010:WorkflowViewState.IdRef="InvokeWorkflowFile_1" />
          </Sequence>
        </Activity>
        """;

    public static async Task<IReadOnlyList<EvalOutcome>> RunAll() {
        var ctx = new GoldenEvalContext();
        return [
            await ExcelForeach(ctx),
            await InvokeArgs(ctx),
            await TryCatchRetry(ctx),
            await IfElse(ctx),
            await Switch(ctx),
            await CodedHelper(ctx),
            await BrokenInvokeFix(ctx),
            await UiaPlaceholderSelectorSucceeds(ctx),
            await UnknownXamlWriteRefused(ctx),
            await RecommendActivitiesHits(ctx),
            await ValidateProjectDiagnostics(ctx),
            await CodedIdiomGaps(ctx),
            await AssignAnnotationArguments(ctx),
            await ExpressionLanguage(ctx),
            await ReadabilityGaps(ctx),
            await Diagrams(ctx),
            await CodedFirstPath(ctx)
        ];
    }

    // ------------------------------------------------------------ eval 01

    public static async Task<EvalOutcome> ExcelForeach(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "01-excel-foreach",
            name: "Excel ForEachRow body shape",
            specFile: "01-excel-foreach.json",
            relativePath: "Workflows/ExcelForeach.xaml",
            families: ["body-shape", "rule24", "idref", "viewstate"],
            assert: s => {
                s.RequireRootActivity().RequireRule24Wraps().RequireIdRefsOnEveryActivity().RequireHintSizeOnEveryActivity();

                var forEachRow = s.Find(EvalNs.Ui, "ForEachRow");
                s.Require(forEachRow is not null, "missing <ui:ForEachRow>");
                if (forEachRow is not null) {
                    // ForEachRow.Body must be ActivityAction<sd:DataRow> + DelegateInArgument CurrentRow.
                    s.RequireTypedActionBody(forEachRow, "Body", "sd:DataRow", "CurrentRow");
                    s.Require(forEachRow.Attribute("DataTable")?.Value == "[dt]",
                        "<ui:ForEachRow> DataTable must be an attribute-form binding");
                }

                // The Sequence root owns its variable scope and shows expanded.
                var rootSequence = s.Find(EvalNs.Wf, "Sequence");
                s.Require(rootSequence is not null, "missing root <Sequence>");
                s.Require(rootSequence?.Element(EvalNs.Wf + "Sequence.Variables") is not null,
                    "root Sequence must declare <Sequence.Variables>");
                var expanded = rootSequence?.Element(EvalNs.ViewState)?.Descendants()
                    .FirstOrDefault(e => e.Attribute(EvalNs.X + "Key")?.Value == "IsExpanded");
                s.Require(expanded is not null, "root Sequence is missing the IsExpanded ViewState");

                // The property element holding .Body must be the ForEachRow's own.
                s.Require(forEachRow is not null && s.PropertyElement(forEachRow, "Body") is not null,
                    "<ui:ForEachRow.Body> property element is missing");
            });

    // ------------------------------------------------------------ eval 02

    public static async Task<EvalOutcome> InvokeArgs(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "02-invoke-args",
            name: "InvokeWorkflowFile Arguments dictionary",
            specFile: "02-invoke-args.json",
            relativePath: "Workflows/InvokeChild.xaml",
            families: ["body-shape", "idref"],
            assert: s => {
                s.RequireRootActivity().RequireIdRefsOnEveryActivity();

                var invoke = s.Find(EvalNs.Ui, "InvokeWorkflowFile");
                s.Require(invoke is not null, "missing <ui:InvokeWorkflowFile>");
                if (invoke is not null) {
                    s.Require(invoke.Attribute("WorkflowFileName")?.Value == "Workflows/Child.xaml",
                        "<ui:InvokeWorkflowFile> WorkflowFileName attribute is wrong");
                    // No activity body: children are the reason the old renderer
                    // silently dropped the Arguments dictionary.
                    s.Require(s.PropertyElement(invoke, "Arguments") is not null,
                        "<ui:InvokeWorkflowFile.Arguments> is missing");
                    s.RequireArgumentDictionary(invoke, ("in_Path", "InArgument", "x:String"), ("out_Ok", "OutArgument", "x:Boolean"));
                }
            });

    // ------------------------------------------------------------ eval 03

    public static async Task<EvalOutcome> TryCatchRetry(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "03-trycatch-retry",
            name: "RetryScope + TryCatch body shapes",
            specFile: "03-trycatch-retry.json",
            relativePath: "Workflows/RetryTryCatch.xaml",
            families: ["body-shape", "rule24", "idref", "viewstate"],
            assert: s => {
                s.RequireRootActivity().RequireRule24Wraps().RequireIdRefsOnEveryActivity().RequireHintSizeOnEveryActivity();

                var retry = s.Find(EvalNs.Ui, "RetryScope");
                s.Require(retry is not null, "missing <ui:RetryScope>");
                if (retry is not null) {
                    // RetryScope.ActivityBody is a BARE ActivityAction (no DelegateInArgument).
                    s.RequireUntypedActionBody(retry, "ActivityBody");
                    s.Require(retry.Attribute("NumberOfRetries")?.Value == "3",
                        "<ui:RetryScope> NumberOfRetries attribute is wrong");
                    s.Require(retry.Attribute("RetryInterval")?.Value == "00:00:05",
                        "<ui:RetryScope> RetryInterval attribute is wrong");
                }

                var tryCatch = s.Find(EvalNs.Wf, "TryCatch");
                s.Require(tryCatch is not null, "missing <TryCatch>");
                if (tryCatch is not null) {
                    // TryCatch.Try holds a Sequence wrap, not a bare activity.
                    s.RequireSequenceSlot(tryCatch, "Try");
                    var catchElement = s.FindAll(EvalNs.Wf, "Catch").SingleOrDefault();
                    s.Require(catchElement is not null, "missing <Catch>");
                    if (catchElement is not null) {
                        s.Require(catchElement.Attribute(EvalNs.X + "TypeArguments")?.Value == "System.Exception",
                            $"<Catch> x:TypeArguments expected System.Exception but was {catchElement.Attribute(EvalNs.X + "TypeArguments")?.Value}");
                        var action = catchElement.Element(EvalNs.Wf + "ActivityAction");
                        s.Require(action is not null, "<Catch> has no <ActivityAction>");
                        s.Require(action?.Element(EvalNs.Wf + "ActivityAction.Argument")?.Element(EvalNs.Wf + "DelegateInArgument") is not null,
                            "<Catch> has no DelegateInArgument");
                    }
                }
            });

    // ------------------------------------------------------------ eval 04

    public static async Task<EvalOutcome> IfElse(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "04-if-else",
            name: "If.Then / If.Else Sequence wraps",
            specFile: "04-if-else.json",
            relativePath: "Workflows/IfElse.xaml",
            families: ["rule24", "idref", "viewstate"],
            assert: s => {
                s.RequireRootActivity().RequireRule24Wraps().RequireIdRefsOnEveryActivity();

                var ifElement = s.Find(EvalNs.Wf, "If");
                s.Require(ifElement is not null, "missing <If>");
                if (ifElement is not null) {
                    s.Require(ifElement.Attribute("Condition")?.Value == "[in_IsValid]",
                        "<If> Condition attribute is wrong");
                    // Both branches must be present and Sequence-wrapped. The old
                    // substring harness passed on a bare If.Then, which is the defect.
                    s.Require(s.PropertyElement(ifElement, "Then") is not null, "<If.Then> is missing");
                    s.Require(s.PropertyElement(ifElement, "Else") is not null, "<If.Else> is missing");
                    s.RequireSequenceSlot(ifElement, "Then");
                    s.RequireSequenceSlot(ifElement, "Else");
                }
            });

    // ------------------------------------------------------------ eval 05

    public static async Task<EvalOutcome> Switch(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "05-switch",
            name: "Switch x:TypeArguments + keyed Sequence cases",
            specFile: "05-switch.json",
            relativePath: "Workflows/SwitchStatus.xaml",
            families: ["rule24", "type-arguments", "idref"],
            assert: s => {
                s.RequireRootActivity().RequireRule24Wraps().RequireIdRefsOnEveryActivity();

                var switchElement = s.Find(EvalNs.Wf, "Switch");
                s.Require(switchElement is not null, "missing <Switch>");
                if (switchElement is null) {
                    return;
                }

                // The renderer emits the resolved x: primitive token, not the raw
                // spec name: x:Int32, never a bare Int32.
                s.Require(switchElement.Attribute(EvalNs.X + "TypeArguments")?.Value == "x:Int32",
                    $"<Switch> x:TypeArguments expected \"x:Int32\" but was \"{switchElement.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");
                s.Require(switchElement.Attribute("Expression")?.Value == "[status]",
                    "<Switch> Expression attribute is wrong");

                var cases = switchElement.Elements()
                    .Where(e => !e.Name.LocalName.Contains('.'))
                    .ToList();
                s.Require(cases.Count == 2, $"expected 2 switch cases but found {cases.Count}");
                foreach (var key in new[] { "1", "2" }) {
                    var caseElement = cases.FirstOrDefault(e => e.Attribute(EvalNs.X + "Key")?.Value == key);
                    s.Require(caseElement?.Name == EvalNs.Wf + "Sequence",
                        $"switch case \"{key}\" must be a keyed <Sequence> but was <{caseElement?.Name.LocalName ?? "missing"}>");
                }

                // The default branch is Sequence-wrapped even though it holds one activity.
                s.RequireSequenceSlot(switchElement, "Default");
            });

    // ------------------------------------------------------------ eval 06

    public static async Task<EvalOutcome> CodedHelper(GoldenEvalContext ctx) {
        var cs = """
            namespace EvalProcess;
            public static class InvoiceHelper {
                public static string Normalize(string value) => value.Trim();
            }
            """;
        var writeCs = await ctx.Write.WriteWorkflowFile(GoldenEvalContext.ProjectPath, "InvoiceHelper.cs", cs);
        if (writeCs.Status != "success") {
            return Fail("06-coded-helper", "Coded helper (InvokeCode + .cs)",
                specValidates: null, xamlEmits: false, unknownWrite: null,
                $"write_workflow_file(.cs) failed: {writeCs.Summary}");
        }

        var author = await Author(
            ctx,
            id: "06-coded-helper",
            name: "InvokeCode Arguments dictionary + .cs",
            specFile: "06-coded-helper.json",
            relativePath: "Workflows/CodedHelper.xaml",
            families: ["body-shape", "idref"],
            assert: s => {
                s.RequireRootActivity().RequireIdRefsOnEveryActivity();

                var invokeCode = s.Find(EvalNs.Ui, "InvokeCode");
                s.Require(invokeCode is not null, "missing <ui:InvokeCode>");
                if (invokeCode is null) {
                    return;
                }

                s.Require(invokeCode.Attribute("Code")?.Value?.Contains("InvoiceHelper.Normalize", StringComparison.Ordinal) == true,
                    "<ui:InvokeCode> Code attribute must carry the helper call");
                s.Require(invokeCode.Attribute("Language")?.Value == "CSharp",
                    "<ui:InvokeCode> Language attribute is wrong");
// InvokeCode takes no activity body; parameters arrive as Arguments.
        s.Require(invokeCode.Elements().All(e => e.Name.LocalName.Contains('.')),
                    "<ui:InvokeCode> must not hold activity children (it takes no body)");
            });
        return author with {
            Detail = author.Passed
                ? "spec validated, XAML emitted with InvokeCode shape, InvoiceHelper.cs written"
                : author.Detail
        };
    }

    // ------------------------------------------------------------ eval 07

    public static async Task<EvalOutcome> BrokenInvokeFix(GoldenEvalContext ctx) {
        var brokenJson = GoldenEvalContext.LoadSpec("07-broken-invoke.invalid.json");
        var broken = await ctx.ValidateSpec.ValidateActivitySpec(brokenJson, GoldenEvalContext.ProjectPath);
        if (broken.Status != "error"
            || !broken.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty)) {
            return Fail("07-broken-invoke-fix", "Broken invoke spec fix",
                specValidates: false, xamlEmits: false, unknownWrite: null,
                "expected SPEC_MISSING_REQUIRED_PROPERTY for InvokeWorkflowFile without WorkflowFileName");
        }

        return await Author(
            ctx,
            id: "07-broken-invoke-fix",
            name: "Broken invoke spec fix",
            specFile: "07-broken-invoke.fixed.json",
            relativePath: "Workflows/FixedInvoke.xaml",
            families: ["body-shape"],
            assert: s => {
                var invoke = s.Find(EvalNs.Ui, "InvokeWorkflowFile");
                s.Require(invoke is not null, "missing <ui:InvokeWorkflowFile>");
                if (invoke is not null) {
                    s.RequireArgumentDictionary(invoke, ("in_Path", "InArgument", "x:String"), ("out_Ok", "OutArgument", "x:Boolean"));
                }
            });
    }

    // ------------------------------------------------------------ eval 08

    /// <summary>
    /// The flipped eval 08. The documented placeholder-selector path
    /// (<c>docs/copilot-studio-skills/rpa-authoring/SKILL.md</c> § UI automation)
    /// emits real <c>uix:</c> activities with placeholder selectors and
    /// <c>TODO Indicate</c> markers. It used to be refused, because UIA was not in
    /// the catalog; the catalog and <c>XamlBuilder</c>'s prefix declarations were
    /// fixed, so the write must now SUCCEED and the emitted XAML must be correctly
    /// prefixed.
    /// </summary>
    public static async Task<EvalOutcome> UiaPlaceholderSelectorSucceeds(GoldenEvalContext ctx) {
        var specJson = EvalSpecs.UiaPlaceholderSelector;
        var validated = await ctx.ValidateSpec.ValidateActivitySpec(specJson, GoldenEvalContext.ProjectPath);
        if (validated.Status != "success") {
            var codes = string.Join("; ", validated.ErrorDetails.Select(e => $"{e.ErrorCode}:{e.Message}"));
            return Fail("08-uia-placeholder-selector", "UIA placeholder-selector path succeeds",
                specValidates: false, xamlEmits: false, unknownWrite: null,
                $"validate_activity_spec rejected the documented UIA path: {codes}");
        }

        var built = await ctx.Build.BuildWorkflow(GoldenEvalContext.ProjectPath, "Workflows/UiaClick.xaml", specJson);
        var target = GoldenEvalContext.Target("Workflows/UiaClick.xaml");
        if (built.Status != "success" || !ctx.Fs.Writes.TryGetValue(target, out var xaml)) {
            return Fail("08-uia-placeholder-selector", "UIA placeholder-selector path succeeds",
                specValidates: true, xamlEmits: false, unknownWrite: null,
                $"build_workflow refused the documented UIA path: {built.Summary} {string.Join(";", built.ErrorDetails.Select(e => e.Message))}");
        }

        var structural = Structural.TryParse(xaml, out var parseError);
        if (parseError is not null) {
            return Fail("08-uia-placeholder-selector", "UIA placeholder-selector path succeeds",
                specValidates: true, xamlEmits: false, unknownWrite: null, $"emitted XAML did not parse: {parseError}");
        }

        // The write must not have needed the escape hatch: prove the guard accepts it.
        var catalog = await TestCatalogs.Resolver(ctx.Fs, ctx.Discovery).ResolveAsync(GoldenEvalContext.ProjectPath);
        var unknown = XamlCatalogGuard.FindUnknownActivities(xaml, catalog);
        structural.Require(unknown.Count == 0,
            $"guard still reports unknown activities: {string.Join(", ", unknown.Select(u => u.Message))}");

        structural.RequireRootActivity().RequireRule24Wraps().RequireIdRefsOnEveryActivity();
        structural.Require(xaml.Contains("xmlns:uix=", StringComparison.Ordinal),
            "the root does not declare the uix: namespace");
        var appCard = structural.Find(EvalNs.Uix, "NApplicationCard");
        var click = structural.Find(EvalNs.Uix, "NClick");
        structural.Require(appCard is not null, "missing <uix:NApplicationCard>");
        structural.Require(click is not null, "missing <uix:NClick>");

        // A wrongly-prefixed element is what a missing uix: declaration produces.
        structural.Require(!xaml.Contains("d1p1:", StringComparison.Ordinal),
            "emitted XAML contains a d1p1: prefix, which means the uix: namespace was not declared");

        // NApplicationCard.Body holds a Rule 24 Sequence; the placeholder-selection
        // shape (empty Target + TODO Indicate DisplayName) must round-trip.
        if (appCard is not null) {
            structural.RequireSequenceSlot(appCard, "Body");
        }

        var parsed = new XamlWorkflowParser().Parse("UiaClick.xaml", "UiaClick.xaml", xaml);
        structural.Require(!parsed.HasParseError, "the emitted UIA workflow did not parse back");
        var parsedClick = parsed.Activities.FirstOrDefault(a => a.Type == "NClick");
        structural.Require(parsedClick is not null, "NClick did not round-trip into ActivityModel");
        structural.Require(parsedClick?.DisplayName.Contains("TODO Indicate", StringComparison.Ordinal) == true,
            $"the TODO Indicate marker did not round-trip (DisplayName was \"{parsedClick?.DisplayName}\")");
        var parsedCard = parsed.Activities.FirstOrDefault(a => a.Type == "NApplicationCard");
        structural.Require(parsedCard?.DisplayName.Contains("TODO Indicate", StringComparison.Ordinal) == true,
            "the NApplicationCard TODO Indicate marker did not round-trip");

        var appCardSlot = parsed.Activities.FirstOrDefault(a => a.Type == "Sequence" && a.Slot == "Body");
        structural.Require(appCardSlot is not null,
            "the NApplicationCard body Sequence did not round-trip with its Body slot");

        return new EvalOutcome(
            "08-uia-placeholder-selector",
            "UIA placeholder-selector path succeeds",
            Passed: structural.Failures.Count == 0,
            SpecValidates: true,
            XamlEmits: true,
            UnknownXamlWriteSucceeded: false,
            Detail: structural.Failures.Count == 0
                ? "uix:NApplicationCard + uix:NClick written with a declared uix: namespace, no d1p1:, TODO Indicate round-trips"
                : string.Join("; ", structural.Failures)) {
            Families = ["uia", "body-shape", "rule24", "idref", "catalog-guard"]
        };
    }

    // ------------------------------------------------------------ eval 08b

    public static async Task<EvalOutcome> UnknownXamlWriteRefused(GoldenEvalContext ctx) {
        var result = await ctx.Write.WriteWorkflowFile(
            GoldenEvalContext.ProjectPath, "Workflows/UnknownClick.xaml", UnknownActivityXaml);
        var wrote = ctx.Fs.Writes.ContainsKey(GoldenEvalContext.Target("Workflows/UnknownClick.xaml"));
        var refused = result.Status == "error"
            && result.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.SpecUnknownActivity)
            && !wrote;
        return new EvalOutcome(
            "08b-unknown-xaml-refused",
            "Genuinely unknown activity still refused",
            Passed: refused,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: !refused,
            Detail: refused
                ? "write_workflow_file refused an activity that is not in the catalog"
                : $"escape hatch opened for an unknown activity: status={result.Status} wrote={wrote}") {
            Families = ["catalog-guard"]
        };
    }

    // ------------------------------------------------------------ eval 09

    public static async Task<EvalOutcome> RecommendActivitiesHits(GoldenEvalContext ctx) {
        var result = await ctx.Recommend.RecommendActivities("read excel range", GoldenEvalContext.ProjectPath);
        if (result.Status != "success") {
            return Fail("09-recommend-activities", "recommend_activities hits",
                specValidates: null, xamlEmits: null, unknownWrite: null,
                result.Summary);
        }

        var data = GoldenEvalContext.Data(result.Data);
        var names = data.GetProperty("activities").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString() ?? "")
            .ToList();
        var hit = names.Contains("ReadRange", StringComparer.OrdinalIgnoreCase)
            || names.Contains("ReadRangeX", StringComparer.OrdinalIgnoreCase);
        var capped = names.Count is > 0 and <= 5;
        return new EvalOutcome(
            "09-recommend-activities",
            "recommend_activities hits",
            Passed: hit && capped,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: null,
            Detail: hit && capped
                ? $"hits={string.Join(",", names)}"
                : $"missing excel read hit or cap violated: [{string.Join(",", names)}]") {
            Families = ["recommend"]
        };
    }

    // ------------------------------------------------------------ eval 10

    public static async Task<EvalOutcome> ValidateProjectDiagnostics(GoldenEvalContext ctx) {
        ctx.Fs.FileContents["/projects/evalProcess/Main.xaml"] = BrokenInvokeXaml;
        ctx.Cli.Result = new UiPathCliResult {
            Success = false,
            Summary = "Validation failed.",
            Errors = ["[validate] Main.xaml: Could not find workflow file 'Missing.xaml'."],
            Validate = new CliStepResult { Executed = true, Success = false },
            Diagnostics = [
                new CliDiagnostic {
                    Message = "Could not find workflow file 'Missing.xaml'.",
                    FilePath = "Main.xaml",
                    IdRef = "InvokeWorkflowFile_1",
                    Property = "WorkflowFileName",
                    Code = "UIPATH_INVOKE"
                }
            ]
        };

        var result = await BackgroundJobs.AwaitFinished(
            ctx.Jobs,
            await ctx.ValidateProject.ValidateProject(GoldenEvalContext.ProjectPath, validate: true, build: false, pack: false));
        var data = GoldenEvalContext.Data(result.Data);
        if (data.GetProperty("diagnostics").GetArrayLength() != 1) {
            return Fail("10-validate-diagnostics", "validate_project activityId/specFix",
                specValidates: null, xamlEmits: null, unknownWrite: null,
                $"expected 1 diagnostic, got {data.GetProperty("diagnostics").GetArrayLength()}");
        }

        var diagnostic = data.GetProperty("diagnostics")[0];
        var activityId = diagnostic.GetProperty("activityId").GetString();
        var property = diagnostic.GetProperty("property").GetString();
        var specFix = diagnostic.GetProperty("specFix");
        var mapped = activityId == "sequence.1/invokeworkflowfile.1"
            && property == "WorkflowFileName"
            && specFix.GetProperty("workflowFile").GetString() == "Main.xaml"
            && specFix.GetProperty("properties").GetProperty("WorkflowFileName").GetString() == "Missing.xaml"
            && !string.IsNullOrWhiteSpace(specFix.GetProperty("hint").GetString());
        return new EvalOutcome(
            "10-validate-diagnostics",
            "validate_project activityId/specFix",
            Passed: mapped,
            SpecValidates: mapped,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: null,
            Detail: mapped
                ? $"activityId={activityId} property={property}"
                : $"unexpected mapping activityId={activityId} property={property}") {
            Families = ["diagnostics"]
        };
    }

    // ------------------------------------------------------------ eval 11

    public static Task<EvalOutcome> CodedIdiomGaps(GoldenEvalContext ctx) {
        _ = ctx;
        const string mainXaml = """
            <Activity x:Class="Main"
                      sap2010:Annotation.AnnotationText="REFramework shell"
                      xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:s="clr-namespace:System;assembly=mscorlib"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence DisplayName="Main Sequence">
                <ui:LogMessage DisplayName="Log start" Level="Info" Message="start" />
                <TryCatch DisplayName="Try invoke">
                  <TryCatch.Try>
                    <ui:InvokeWorkflowFile DisplayName="Process" WorkflowFileName="Process.xaml" />
                  </TryCatch.Try>
                  <TryCatch.Catch>
                    <Catch x:TypeArguments="s:Exception">
                      <ActivityAction x:TypeArguments="s:Exception">
                        <ui:LogMessage DisplayName="Log error" Level="Error" Message="failed" />
                      </ActivityAction>
                    </Catch>
                  </TryCatch.Catch>
                </TryCatch>
              </Sequence>
            </Activity>
            """;
        const string processXaml = """
            <Activity x:Class="Process"
                      sap2010:Annotation.AnnotationText="Invoice process"
                      xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence DisplayName="Process">
                <ui:ReadRangeX DisplayName="Read invoices" />
              </Sequence>
            </Activity>
            """;
        const string coded = """
            using UiPath.CodedWorkflows;
            public class InvoiceFlow : CodedWorkflow
            {
                [Workflow]
                public void Execute()
                {
                    Run();
                }
            }
            """;
        const string codedTest = """
            using UiPath.CodedWorkflows;
            public class InvoiceTests : CodedWorkflow
            {
                [TestCase]
                public void Execute()
                {
                }
            }
            """;

        var xamlParser = new XamlWorkflowParser();
        var codedParser = new CodedSourceFileParser();
        var main = xamlParser.Parse("Main.xaml", GoldenEvalContext.Target("Main.xaml"), mainXaml);
        main.IsMain = true;
        var process = xamlParser.Parse("Process.xaml", GoldenEvalContext.Target("Process.xaml"), processXaml);
        var invoice = codedParser.Parse("InvoiceFlow.cs", GoldenEvalContext.Target("InvoiceFlow.cs"), coded);
        var tests = codedParser.Parse("InvoiceTests.cs", GoldenEvalContext.Target("Tests/InvoiceTests.cs"), codedTest);

        var model = new UiPathProjectModel {
            ProjectPath = GoldenEvalContext.ProjectPath,
            ProjectName = "EvalProcess",
            MainWorkflow = "Main.xaml",
            Workflows = [main, process],
            CodedWorkflows = [invoice, tests]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);
        var codedTry = gaps.Exists(g => g.Id == "coded-no-exception-handling:InvoiceFlow.cs");
        var codedLog = gaps.Exists(g => g.Id == "coded-no-logging:InvoiceFlow.cs");
        var preferCoded = gaps.Exists(g => g.Id == "xaml-business-logic:Process.xaml");
        var passed = codedTry && codedLog && preferCoded;
        var missing = new List<string>();
        if (!codedTry) {
            missing.Add("coded-no-exception-handling");
        }

        if (!codedLog) {
            missing.Add("coded-no-logging");
        }

        if (!preferCoded) {
            missing.Add("xaml-business-logic:Process.xaml");
        }

        return Task.FromResult(new EvalOutcome(
            "11-coded-idiom-gaps",
            "Coded try/log + XAML Excel prefer-coded",
            Passed: passed,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: null,
            Detail: passed
                ? "coded resilience/observability and Process.xaml prefer-coded gaps fired"
                : $"missing gaps: {string.Join(", ", missing)}") {
            Families = ["gap-analysis"]
        });
    }

    // ------------------------------------------------------------ eval 12

    /// <summary>
    /// Assign generic form, annotation round-trip, and &lt;x:Members&gt;. An
    /// Assign without <c>TypeArgument</c> can never render <c>Assign&lt;T&gt;</c>,
    /// and the builder emitted no Members block at all before the authoring chain
    /// landed.
    /// </summary>
    public static async Task<EvalOutcome> AssignAnnotationArguments(GoldenEvalContext ctx) {
        var result = await ctx.Build.BuildWorkflow(
            GoldenEvalContext.ProjectPath, "Workflows/Assign.xaml", EvalSpecs.AssignAnnotationArguments);
        var target = GoldenEvalContext.Target("Workflows/Assign.xaml");
        if (result.Status != "success" || !ctx.Fs.Writes.TryGetValue(target, out var xaml)) {
            return Fail("12-assign-annotation-arguments", "Assign<T> + annotation + x:Members",
                specValidates: true, xamlEmits: false, unknownWrite: null,
                $"build_workflow failed: {result.Summary} {string.Join(";", result.ErrorDetails.Select(e => e.Message))}");
        }

        var structural = Structural.TryParse(xaml, out var parseError);
        if (parseError is not null) {
            return Fail("12-assign-annotation-arguments", "Assign<T> + annotation + x:Members",
                specValidates: true, xamlEmits: false, unknownWrite: null, $"emitted XAML did not parse: {parseError}");
        }

        structural.RequireRootActivity().RequireIdRefsOnEveryActivity().RequireTypedAssign("x:Int32");

        // <x:Members> with one <x:Property> per declared workflow argument.
        var members = structural.Find(EvalNs.X, "Members");
        structural.Require(members is not null, "missing <x:Members>");
        var properties = members?.Elements(EvalNs.X + "Property").ToList() ?? [];
        structural.Require(properties.Count == 2, $"<x:Members> expected 2 <x:Property> but had {properties.Count}");
        structural.Require(properties.Any(p => p.Attribute("Name")?.Value == "in_Path" && p.Attribute("Type")?.Value == "InArgument(x:String)"),
            "<x:Members> is missing the in_Path InArgument(x:String) property");
        structural.Require(properties.Any(p => p.Attribute("Name")?.Value == "out_Ok" && p.Attribute("Type")?.Value == "OutArgument(x:Boolean)"),
            "<x:Members> is missing the out_Ok OutArgument(x:Boolean) property");

        // Annotation round-trip, per the p8 todo: Annotation in the spec ->
        // sap2010:Annotation.AnnotationText in the XAML -> parsed back into
        // ActivityModel.Annotation.
        var assign = structural.Find(EvalNs.Wf, "Assign");
        var rootSequence = structural.Find(EvalNs.Wf, "Sequence");
        if (assign is not null) {
            structural.RequireAnnotationRoundTrip(assign, "assign note");
        }

        if (rootSequence is not null) {
            structural.RequireAnnotationRoundTrip(rootSequence, "root note");
        }

        // WorkflowModel.Description round-trip: the root activity annotation is the
        // workflow-level description, so a spec-built workflow is documented rather
        // than leaving ProjectGapAnalyzer's description-coverage and
        // workflow-no-description rules firing on a workflow that visibly has one.
        var parsed = new XamlWorkflowParser().Parse("Assign.xaml", "Assign.xaml", xaml);
        var parsedAssign = parsed.Activities.FirstOrDefault(a => a.Type == "Assign");
        structural.Require(parsedAssign?.Annotation == "assign note",
            $"the Assign annotation did not round-trip into ActivityModel.Annotation (got \"{parsedAssign?.Annotation}\")");
        var parsedRoot = parsed.Activities.FirstOrDefault(a => a.Type == "Sequence");
        structural.Require(parsedRoot?.Annotation == "root note",
            $"the root Sequence annotation did not round-trip into ActivityModel.Annotation (got \"{parsedRoot?.Annotation}\")");
        structural.Require(parsed.Description == "root note",
            $"the root annotation did not populate WorkflowModel.Description (got \"{parsed.Description}\")");
        structural.Require(parsed.Arguments.Any(a => a.Name == "in_Path" && a.Direction == "In" && a.Type == "x:String"),
            "the in_Path argument did not round-trip into WorkflowModel.Arguments");

        return new EvalOutcome(
            "12-assign-annotation-arguments",
            "Assign<T> + annotation + x:Members",
            Passed: structural.Failures.Count == 0,
            SpecValidates: true,
            XamlEmits: true,
            UnknownXamlWriteSucceeded: null,
            Detail: structural.Failures.Count == 0
                ? "Assign<x:Int32> with typed To/Value, annotation round-trips, x:Members carries both arguments"
                : string.Join("; ", structural.Failures)) {
            Families = ["type-arguments", "annotation", "x-members", "idref"]
        };
    }

    // ------------------------------------------------------------ eval 13

    /// <summary>
    /// One spec, two projects. VisualBasic must bind through the attribute /
    /// <c>[bracket]</c> form and emit <c>VisualBasic.Settings</c>; CSharp must
    /// bind a non-literal expression through typed <c>CSharpValue</c> /
    /// <c>CSharpReference</c> property elements and emit
    /// <c>TextExpression.NamespacesForImplementation</c>. A <c>[bracket]</c>
    /// value in a C# project must be refused, because it deserializes as
    /// <c>VisualBasicValue&lt;T&gt;</c> and fails at runtime.
    /// </summary>
    public static async Task<EvalOutcome> ExpressionLanguage(GoldenEvalContext _) {
        var vb = GoldenEvalContext.VisualBasic();
        var cs = GoldenEvalContext.CSharp();

        var vbBuild = await vb.Build.BuildWorkflow(GoldenEvalContext.ProjectPath, "Workflows/Shared.xaml", EvalSpecs.SharedLiteralMessage);
        var csBuild = await cs.Build.BuildWorkflow(GoldenEvalContext.ProjectPath, "Workflows/Shared.xaml", EvalSpecs.SharedLiteralMessage);
        var vbTarget = GoldenEvalContext.Target("Workflows/Shared.xaml");
        var csTarget = GoldenEvalContext.Target("Workflows/Shared.xaml");

        if (vbBuild.Status != "success" || !vb.Fs.Writes.TryGetValue(vbTarget, out var vbXaml)) {
            return Fail("13-expression-language", "VB vs C# binding form from one spec",
                specValidates: true, xamlEmits: false, unknownWrite: null,
                $"VB build failed: {vbBuild.Summary} {string.Join(";", vbBuild.ErrorDetails.Select(e => e.Message))}");
        }

        if (csBuild.Status != "success" || !cs.Fs.Writes.TryGetValue(csTarget, out var csXaml)) {
            return Fail("13-expression-language", "VB vs C# binding form from one spec",
                specValidates: true, xamlEmits: false, unknownWrite: null,
                $"C# build failed: {csBuild.Summary} {string.Join(";", csBuild.ErrorDetails.Select(e => e.Message))}");
        }

        var vbStructural = Structural.TryParse(vbXaml, out var vbError);
        var csStructural = Structural.TryParse(csXaml, out var csError);
        if (vbError is not null || csError is not null) {
            return Fail("13-expression-language", "VB vs C# binding form from one spec",
                specValidates: true, xamlEmits: false, unknownWrite: null,
                $"emitted XAML did not parse: VB={vbError} C#={csError}");
        }

        vbStructural.RequireExpressionForm(csharp: false);
        csStructural.RequireExpressionForm(csharp: true, expectValue: true, expectReference: false);

        // The VB form is the attribute on the activity; the C# form is a typed
        // CSharpValue property element under LogMessage.Message.
        var vbLog = vbStructural.Find(EvalNs.Ui, "LogMessage");
        vbStructural.Require(vbLog?.Attribute("Message")?.Value is not null,
            "the VB project did not render Message in attribute form");
        var csLog = csStructural.Find(EvalNs.Ui, "LogMessage");
        csStructural.Require(csLog is not null, "the C# project emitted no <ui:LogMessage>");
        if (csLog is not null) {
            var csharpValue = csStructural.CSharpBinding(csLog, "Message", "CSharpValue");
            csStructural.Require(csharpValue is not null,
                "the C# project did not render Message as a typed <CSharpValue> property element");
            csStructural.Require(csharpValue?.Value.Contains("hello", StringComparison.Ordinal) == true,
                "the C# CSharpValue does not carry the expression text");
            csStructural.Require(csStructural.PropertyElement(csLog, "Message")?.Element(EvalNs.Wf + "InArgument")?.Attribute(EvalNs.X + "TypeArguments")?.Value == "x:Object",
                "the C# LogMessage.Message InArgument must carry x:TypeArguments=\"x:Object\"");
        }

        // A write binding must be a CSharpReference: the Assign eval covers it, and
        // here we assert the C# project never renders a VB attribute expression.
        csStructural.Require(csLog?.Attribute("Message") is null,
            "the C# project rendered an expression in attribute form");

        // The language rule is enforced on the way in: a bracket value is refused
        // for a C# project but accepted for a VB one.
        var vbBracket = await vb.ValidateSpec.ValidateActivitySpec(EvalSpecs.BracketShorthand, GoldenEvalContext.ProjectPath);
        var csBracket = await cs.ValidateSpec.ValidateActivitySpec(EvalSpecs.BracketShorthand, GoldenEvalContext.ProjectPath);
        vbStructural.Require(vbBracket.Status == "success", "a VB project must accept [bracket] shorthand");
        csStructural.Require(csBracket.Status == "error"
                && csBracket.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch),
            "a C# project must reject [bracket] shorthand with SPEC_VALUE_FORM_MISMATCH");

        var failures = vbStructural.Failures.Concat(csStructural.Failures).ToList();
        return new EvalOutcome(
            "13-expression-language",
            "VB vs C# binding form from one spec",
            Passed: failures.Count == 0,
            SpecValidates: true,
            XamlEmits: true,
            UnknownXamlWriteSucceeded: null,
            Detail: failures.Count == 0
                ? "VB attribute form + VisualBasic.Settings; C# CSharpValue/CSharpReference + NamespacesForImplementation; [bracket] refused under C#"
                : string.Join("; ", failures)) {
            Families = ["expression-language"]
        };
    }

    // ------------------------------------------------------------ eval 14

    /// <summary>
    /// The <c>readability</c> gap category: DisplayName quality, directional
    /// argument prefixes, nesting/activity counts, Rule 24 container-body wraps,
    /// description coverage, and REFramework conformance — each carrying a
    /// <see cref="Gap.Confidence"/> so a namespace-blind heuristic is marked as a
    /// hint rather than asserted as fact.
    /// </summary>
    public static Task<EvalOutcome> ReadabilityGaps(GoldenEvalContext ctx) {
        _ = ctx;

        static ActivityModel Activity(string type, string displayName, int depth, string id) =>
            new() { Id = id, Type = type, DisplayName = displayName, Depth = depth };

        // One workflow with generic DisplayNames, an un-prefixed argument, a
        // container whose two children are not Sequence-wrapped, and deep nesting.
        var workflow = new WorkflowModel {
            FileName = "LatestInvoice.xaml",
            FilePath = "/projects/evalProcess/LatestInvoice.xaml"
        };
        workflow.Arguments.Add(new ArgumentModel { Name = "InvoicePath", Direction = "In", Type = "x:String" });
        workflow.Activities.Add(Activity("Sequence", "Sequence1", 1, "sequence.1"));
        workflow.Activities.Add(Activity("Sequence", "LatestInvoice", 2, "sequence.1/sequence.1"));
        // A While with two direct children: its body slot holds a single Activity,
        // so 2+ children is a Rule 24 violation.
        workflow.Activities.Add(Activity("InterruptibleWhile", "While", 3, "sequence.1/sequence.1/interruptiblewhile.1"));
        workflow.Activities.Add(Activity("LogMessage", "LogMessage1", 4, "sequence.1/sequence.1/interruptiblewhile.1/logmessage.1"));
        workflow.Activities.Add(Activity("WriteLine", "WriteLine1", 4, "sequence.1/sequence.1/interruptiblewhile.1/writeline.1"));
        // Deep nesting past the guideline.
        var deep = "sequence.1/sequence.1/interruptiblewhile.1/logmessage.1";
        for (var depth = 5; depth <= 9; depth++) {
            deep += "/sequence.1";
            workflow.Activities.Add(Activity("Sequence", "Nested", depth, deep));
        }

        // Attach children so the container/child counts are structural.
        var byId = workflow.Activities.ToDictionary(a => a.Id, StringComparer.Ordinal);
        foreach (var activity in workflow.Activities) {
            if (activity.ParentId is null || !byId.TryGetValue(activity.ParentId, out var parent)) {
                // ParentId is computed by the locator in production; rebuild the
                // parent links from the structural path here so the linter sees them.
                var slash = activity.Id.LastIndexOf('/');
                if (slash > 0 && byId.TryGetValue(activity.Id[..slash], out var owner)) {
                    owner.Children.Add(activity);
                }
            } else {
                parent.Children.Add(activity);
            }
        }

        var model = new UiPathProjectModel {
            ProjectPath = GoldenEvalContext.ProjectPath,
            ProjectName = "EvalProcess",
            OutputType = "Process",
            MainWorkflow = "Main.xaml",
            // The description-coverage rule only reports project-wide advice once at
            // least three workflows are parsed; two of the three here are
            // undocumented, so the advice fires.
            Workflows = [
                workflow,
                new WorkflowModel { FileName = "Other.xaml", FilePath = "/projects/evalProcess/Other.xaml", Description = "Documented." },
                new WorkflowModel { FileName = "Third.xaml", FilePath = "/projects/evalProcess/Third.xaml" }
            ]
        };

        var gaps = ProjectGapAnalyzer.Analyze(model);
        var readability = gaps.Where(g => g.Category == Gap.CategoryReadability).ToList();

        var failures = new List<string>();
        void Expect(string id, string? confidence = null) {
            var gap = readability.FirstOrDefault(g => g.Id == id);
            if (gap is null) {
                failures.Add($"missing readability gap '{id}'");
                return;
            }

            if (confidence is not null && gap.Confidence != confidence) {
                failures.Add($"gap '{id}' confidence expected '{confidence}' but was '{gap.Confidence}'");
            }
        }

        Expect("generic-displayname:LatestInvoice.xaml", Gap.ConfidenceLow);
        Expect("argument-prefix-convention:LatestInvoice.xaml", Gap.ConfidenceHigh);
        Expect("deep-container-nesting:LatestInvoice.xaml", Gap.ConfidenceHigh);
        Expect("unwrapped-container-body:LatestInvoice.xaml:sequence.1/sequence.1/interruptiblewhile.1", Gap.ConfidenceMedium);
        Expect("description-coverage-low", Gap.ConfidenceHigh);

        // Every readability gap must be classed as such and rank between
        // observability and documentation in the reporting order.
        if (readability.Count == 0) {
            failures.Add("no readability gaps fired at all");
        }

        var rank = Gap.CategoryRank(Gap.CategoryReadability);
        if (rank <= Gap.CategoryRank("observability") || rank >= Gap.CategoryRank("documentation")) {
            failures.Add("the readability category does not rank between observability and documentation");
        }

        if (readability.Any(g => string.IsNullOrWhiteSpace(g.Confidence))) {
            failures.Add("a readability gap carries no confidence");
        }

        return Task.FromResult(new EvalOutcome(
            "14-readability-gaps",
            "readability category + confidence",
            Passed: failures.Count == 0,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: null,
            Detail: failures.Count == 0
                ? $"fired {readability.Count} readability gap(s): {string.Join(", ", readability.Select(g => g.Id))}"
                : string.Join("; ", failures)) {
            Families = ["gap-analysis", "readability"]
        });
    }

    // ------------------------------------------------------------ eval 15

    /// <summary>
    /// Rule 20 structure-first for Flowchart and StateMachine: nodes are direct
    /// children wired with <c>x:Reference</c>, every node carries a mandatory
    /// ShapeLocation + ShapeSize, and there are no orphan nodes.
    /// </summary>
    public static Task<EvalOutcome> Diagrams(GoldenEvalContext ctx) {
        _ = ctx;

        var failures = new List<string>();

        var flow = XamlBuilder.RenderWorkflowFile(BuildSpec(EvalSpecs.Flowchart), "FlowchartEval");
        if (flow.Xaml is null) {
            failures.Add($"flowchart render failed: {string.Join(";", flow.Errors.Select(e => e.Message))}");
        } else {
            var s = Structural.Parse(flow.Xaml);
            s.RequireRootActivity().RequireIdRefsOnEveryActivity();
            s.Require(RootDeclares(s, "av"), "the flowchart root does not declare the av: prefix");
            s.RequireDiagram(EvalNs.Wf, "Flowchart", "FlowStep", "FlowDecision", "FlowSwitch");
            failures.AddRange(s.Failures);
        }

        var machine = XamlBuilder.RenderWorkflowFile(BuildSpec(EvalSpecs.StateMachine), "StateMachineEval");
        if (machine.Xaml is null) {
            failures.Add($"state machine render failed: {string.Join(";", machine.Errors.Select(e => e.Message))}");
        } else {
            var s = Structural.Parse(machine.Xaml);
            s.RequireRootActivity().RequireIdRefsOnEveryActivity();
            s.Require(RootDeclares(s, "av"), "the state machine root does not declare the av: prefix");
            s.RequireDiagram(EvalNs.Wf, "StateMachine", "State", "FinalState");
            // InitialState wires the start with x:Reference.
            s.Require(s.Find(EvalNs.Wf, "StateMachine")?.Attribute("InitialState")?.Value.StartsWith("{x:Reference", StringComparison.Ordinal) == true,
                "<StateMachine> InitialState must be an x:Reference");
            failures.AddRange(s.Failures);
        }

        // The parsed model must keep the graph wiring instead of flattening it away.
        if (flow.Xaml is not null) {
            var parsed = new XamlWorkflowParser().Parse("FlowchartEval.xaml", "FlowchartEval.xaml", flow.Xaml);
            var step = parsed.Activities.FirstOrDefault(a => a.Type == "FlowStep");
            if (step?.GraphLinks.ContainsKey("Next") != true) {
                failures.Add("FlowStep.Next did not round-trip into ActivityModel.GraphLinks");
            }

            var decision = parsed.Activities.FirstOrDefault(a => a.Type == "FlowDecision");
            if (decision?.GraphLinks.ContainsKey("True") != true) {
                failures.Add("FlowDecision.True did not round-trip into ActivityModel.GraphLinks");
            }
        }

        return Task.FromResult(new EvalOutcome(
            "15-diagrams",
            "Flowchart / StateMachine structure-first",
            Passed: failures.Count == 0,
            SpecValidates: null,
            XamlEmits: failures.Count == 0,
            UnknownXamlWriteSucceeded: null,
            Detail: failures.Count == 0
                ? "nodes are direct children, wired by x:Reference, ShapeLocation+ShapeSize present, no orphans, graph round-trips"
                : string.Join("; ", failures)) {
            Families = ["diagram", "rule20", "idref", "viewstate"]
        });
    }

    private static ActivitySpec BuildSpec(string json) {
        if (!SpecJson.TryDeserialize(json, out var spec, out var error)) {
            throw new InvalidOperationException(error!.Message);
        }

        return spec!;
    }

    private static bool RootDeclares(Structural structural, string prefix) =>
        structural.Root.Attribute(XNamespace.Xmlns + prefix) is not null;

    // ------------------------------------------------------------ eval 16

    /// <summary>
    /// The coded-first default path: <c>add_coded_workflow</c> kinds and where
    /// each registers, the Roslyn <c>analysisMode</c> tiers, the
    /// <c>search_codebase</c> modes, and the <c>ACTIVITY_ID_STALE</c> guard.
    /// These have ordinary xUnit tests but had no golden eval.
    /// </summary>
    public static async Task<EvalOutcome> CodedFirstPath(GoldenEvalContext ctx) {
        _ = ctx;
        var failures = new List<string>();

        // ---- add_coded_workflow kinds -> entryPoints vs fileInfoCollection ----------------
        var fs = new FakeFilesystemProvider {
            ProjectJson = GoldenEvalContext.ProjectPath + "/project.json",
            ProjectJsonContent = """
                { "name": "EvalProcess", "main": "Main.xaml",
                  "dependencies": { "UiPath.System.Activities": "[24.10.0]" },
                  "designOptions": { "outputType": "Process" } }
                """
        };
        var codedTool = new CreateCodedWorkflowTool(fs);
        var workflow = codedTool.AddCodedWorkflow(GoldenEvalContext.ProjectPath, "InvoiceFlow", "workflow");
        var test = codedTool.AddCodedWorkflow(GoldenEvalContext.ProjectPath, "InvoiceTests", "test");
        var source = codedTool.AddCodedWorkflow(GoldenEvalContext.ProjectPath, "InvoiceHelper", "source");
        if (workflow.Status != "success" || test.Status != "success" || source.Status != "success") {
            failures.Add($"add_coded_workflow failed: {workflow.Summary} | {test.Summary} | {source.Summary}");
        }

        var patched = fs.Writes.GetValueOrDefault(GoldenEvalContext.ProjectPath + "/project.json") ?? "";
        using (var doc = JsonDocument.Parse(patched)) {
            var root = doc.RootElement;
            var entryPoints = root.TryGetProperty("entryPoints", out var eps)
                ? eps.EnumerateArray().Select(e => e.GetProperty("filePath").GetString()).ToList()
                : [];
            var fileInfo = root.TryGetProperty("designOptions", out var design)
                && design.TryGetProperty("fileInfoCollection", out var fic)
                ? fic.EnumerateArray().Select(e => e.GetProperty("fileName").GetString()).ToList()
                : [];

            if (!entryPoints.Contains("InvoiceFlow.cs")) {
                failures.Add("kind=workflow did not register InvoiceFlow.cs in entryPoints");
            }

            if (entryPoints.Contains("InvoiceTests.cs")) {
                failures.Add("kind=test must never register in entryPoints");
            }

            if (!fileInfo.Any(f => f is not null && f.Replace('\\', '/') == "Tests/InvoiceTests.cs")) {
                failures.Add($"kind=test did not register Tests/InvoiceTests.cs in fileInfoCollection (got {string.Join(",", fileInfo)})");
            }

            if (fileInfo.Any(f => f is not null && f.Contains("InvoiceHelper", StringComparison.Ordinal))) {
                failures.Add("kind=source must not register anywhere in project.json");
            }
        }

        // ---- Roslyn analysisMode tiers --------------------------------------------------
        // Full: no dependencies, framework resolved.
        var fullFs = new FakeFilesystemProvider {
            ProjectJson = GoldenEvalContext.ProjectPath + "/project.json",
            ProjectJsonContent = """{ "name": "p", "targetFramework": "net8.0", "dependencies": {} }"""
        };
        fullFs.FileContents[GoldenEvalContext.ProjectPath + "/Flow.cs"] = "namespace P; public class Flow { }";
        fullFs.CSharpFiles.Add(GoldenEvalContext.ProjectPath + "/Flow.cs");
        var fullContext = await new CSharpContextBuilder(fullFs, new NuGetReferenceResolver("/nonexistent-nuget-folder")).BuildAsync(GoldenEvalContext.ProjectPath);
        if (fullContext.Mode != CSharpAnalysisMode.Full) {
            failures.Add($"analysisMode expected Full but was {fullContext.Mode}");
        }

        // SyntaxOnly: dependencies declared but the packages folder is absent.
        var syntaxFs = new FakeFilesystemProvider {
            ProjectJson = GoldenEvalContext.ProjectPath + "/project.json",
            ProjectJsonContent = """{ "name": "p", "targetFramework": "net6.0", "dependencies": { "UiPath.System.Activities": "24.10.4" } }"""
        };
        syntaxFs.FileContents[GoldenEvalContext.ProjectPath + "/Flow.cs"] = "namespace P; public class Flow { }";
        syntaxFs.CSharpFiles.Add(GoldenEvalContext.ProjectPath + "/Flow.cs");
        var syntaxContext = await new CSharpContextBuilder(syntaxFs, new NuGetReferenceResolver("/nonexistent-nuget-folder")).BuildAsync(GoldenEvalContext.ProjectPath);
        if (syntaxContext.Mode != CSharpAnalysisMode.SyntaxOnly) {
            failures.Add($"analysisMode expected SyntaxOnly but was {syntaxContext.Mode}");
        }

        // Partial: packages folder found but the referenced package is not in it.
        var partialFs = new FakeFilesystemProvider {
            ProjectJson = GoldenEvalContext.ProjectPath + "/project.json",
            ProjectJsonContent = """{ "name": "p", "targetFramework": "net8.0", "dependencies": { "Not.Installed": "1.0.0" } }"""
        };
        partialFs.FileContents[GoldenEvalContext.ProjectPath + "/Flow.cs"] = "namespace P; public class Flow { }";
        partialFs.CSharpFiles.Add(GoldenEvalContext.ProjectPath + "/Flow.cs");
        var partialContext = await new CSharpContextBuilder(partialFs, new NuGetReferenceResolver(Path.GetTempPath())).BuildAsync(GoldenEvalContext.ProjectPath);
        if (partialContext.Mode != CSharpAnalysisMode.Partial) {
            failures.Add($"analysisMode expected Partial but was {partialContext.Mode}");
        }

        // The mode must be carried to the wire as lowercase camelCase.
        if (await ModeFor(fullContext) != "full" || await ModeFor(syntaxContext) != "syntaxOnly" || await ModeFor(partialContext) != "partial") {
            failures.Add("symbol search did not carry the analysisMode tier to the result");
        }

        // ---- search_codebase modes -----------------------------------------------------
        var searchFake = new FakeCodebaseSearchService {
            TextResult = new TextSearchResult { Matches = [new TextMatch { FilePath = "Main.xaml", Line = 1, Snippet = "queue" }], FilesSearched = 1 },
            SymbolResult = new SymbolSearchResult { Matches = [new SymbolMatch { Name = "Queue", Kind = "class", FilePath = "Flow.cs", Line = 1 }], AnalysisMode = "full" },
            ActivityResult = new ActivitySearchResult { Matches = [new ActivityMatch { WorkflowFile = "Main.xaml", DisplayName = "Log", ActivityType = "LogMessage" }], WorkflowsSearched = 1 },
            WorkflowResult = new WorkflowSearchResult { Matches = [new WorkflowMatch { FileName = "Main.xaml", FilePath = "/p/Main.xaml", IsMain = true }] }
        };
        var searchTool = new SearchCodebaseTool(new FakeFilesystemProvider { Allowed = true, ProjectJson = "/p/project.json" }, searchFake);
        foreach (var (mode, expected) in new[] { ("text", "1 text match"), ("symbol", "1 symbol"), ("activity", "1 activity match"), ("workflow", "1 workflow") }) {
            var result = await searchTool.SearchCodebase("/p", "queue", mode);
            if (result.Status != "success" || !result.Summary.Contains(expected, StringComparison.Ordinal)) {
                failures.Add($"search_codebase mode '{mode}' summary unexpected: {result.Summary}");
            }
        }

        // An unknown mode is refused rather than silently defaulted.
        var badMode = await searchTool.SearchCodebase("/p", "queue", "semantic");
        if (badMode.Status != "error" || !badMode.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.InvalidArgument)) {
            failures.Add("search_codebase accepted an unknown mode");
        }

        // ---- ACTIVITY_ID_STALE ---------------------------------------------------------
        const string xaml = """
            <Activity x:Class="Main" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence DisplayName="Main Sequence" sap2010:WorkflowViewState.IdRef="Sequence_1">
                <ui:LogMessage DisplayName="Log one" Message="one" Level="Info" sap2010:WorkflowViewState.IdRef="LogMessage_1" />
                <ui:LogMessage DisplayName="Log two" Message="two" Level="Info" sap2010:WorkflowViewState.IdRef="LogMessage_2" />
              </Sequence>
            </Activity>
            """;
        var editFs = new FakeFilesystemProvider {
            ProjectJson = GoldenEvalContext.ProjectPath + "/project.json",
            ProjectJsonContent = """{ "name": "p", "main": "Main.xaml", "dependencies": {} }"""
        };
        var mainPath = GoldenEvalContext.Target("Main.xaml");
        editFs.FileContents[mainPath] = xaml;
        var editTool = new EditWorkflowActivityTool(editFs);

        var stale = editTool.EditWorkflowActivity(
            GoldenEvalContext.ProjectPath, "Main.xaml", "insert", fragment: "<ui:LogMessage DisplayName=\"Log three\" Message=\"three\" />",
            activityId: "sequence.1/logmessage.1", activityType: "Sequence");
        if (stale.Status != "error" || !stale.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.ActivityIdStale)) {
            failures.Add($"a stale activityId must be refused with ACTIVITY_ID_STALE (got {stale.Status})");
        }

        // A fresh, matching ID succeeds and reports the note that IDs may have shifted.
        var fresh = editTool.EditWorkflowActivity(
            GoldenEvalContext.ProjectPath, "Main.xaml", "insert", fragment: "<ui:LogMessage DisplayName=\"Log three\" Message=\"three\" />",
            activityId: "sequence.1", activityType: "Sequence");
        if (fresh.Status != "success") {
            failures.Add($"a matching activityId must succeed (got {fresh.Status}: {fresh.Summary})");
        } else if (!fresh.Warnings.Any(w => w.Contains("per-parse-snapshot", StringComparison.Ordinal))) {
            failures.Add("a successful edit must warn that activity IDs are per-parse-snapshot");
        }

        return new EvalOutcome(
            "16-coded-first-path",
            "coded-first default path",
            Passed: failures.Count == 0,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: null,
            Detail: failures.Count == 0
                ? "add_coded_workflow kinds register correctly, analysisMode tiers Full/Partial/SyntaxOnly, 4 search modes + refusal, ACTIVITY_ID_STALE enforced"
                : string.Join("; ", failures)) {
            Families = ["coded-first", "analysis-mode", "search", "activity-id"]
        };
    }

    private static async Task<string> ModeFor(CSharpAnalysisContext context) {
        var service = new CodebaseSearchService(
            new StubContextBuilder(context), new ThrowingModelBuilder(), new FakeFilesystemProvider());
        var result = await service.SearchSymbolsAsync(GoldenEvalContext.ProjectPath, "Flow");
        return result.AnalysisMode;
    }

    private sealed class StubContextBuilder(CSharpAnalysisContext context) : ICSharpContextBuilder {
        public Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(context);
    }

    private sealed class ThrowingModelBuilder : IProjectModelBuilder {
        public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("symbol search must not build the project model");
    }

    // ------------------------------------------------------------ shared author

    private static async Task<EvalOutcome> Author(
        GoldenEvalContext ctx,
        string id,
        string name,
        string specFile,
        string relativePath,
        Action<Structural> assert,
        IReadOnlyList<string> families) {
        var specJson = GoldenEvalContext.LoadSpec(specFile);
        var validated = await ctx.ValidateSpec.ValidateActivitySpec(specJson, GoldenEvalContext.ProjectPath);
        var specOk = validated.Status == "success";
        if (!specOk) {
            var codes = string.Join("; ", validated.ErrorDetails.Select(e => $"{e.ErrorCode}:{e.Message}"));
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"validate_activity_spec failed: {codes}") with { Families = families };
        }

        var built = await ctx.Build.BuildWorkflow(GoldenEvalContext.ProjectPath, relativePath, specJson);
        var target = GoldenEvalContext.Target(relativePath);
        if (built.Status != "success" || !ctx.Fs.Writes.TryGetValue(target, out var xaml)) {
            var codes = string.Join("; ", built.ErrorDetails.Select(e => $"{e.ErrorCode}:{e.Message}"));
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"build_workflow failed: {built.Summary} {codes}") with { Families = families };
        }

        var structural = Structural.TryParse(xaml, out var parseError);
        if (parseError is not null) {
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"emitted XAML did not parse: {parseError}") with { Families = families };
        }

        assert(structural);
        if (structural.Failures.Count > 0) {
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                string.Join("; ", structural.Failures)) with { Families = families };
        }

        return new EvalOutcome(id, name, Passed: true, SpecValidates: true, XamlEmits: true,
            UnknownXamlWriteSucceeded: null, Detail: $"wrote {relativePath}") {
            Families = families
        };
    }

    private static EvalOutcome Fail(
        string id, string name, bool? specValidates, bool? xamlEmits, bool? unknownWrite, string detail) =>
        new(id, name, Passed: false, specValidates, xamlEmits, unknownWrite, detail);
}