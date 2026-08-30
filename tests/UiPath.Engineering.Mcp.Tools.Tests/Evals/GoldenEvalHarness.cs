using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
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
    string Detail);

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

    public GoldenEvalContext() {
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
        ValidateSpec = new ValidateActivitySpecTool(resolver);
        Build = new BuildWorkflowTool(Fs, resolver);
        Write = new WriteWorkflowFileTool(Fs, resolver);
        Recommend = new RecommendActivitiesTool(Fs, resolver);
        ValidateProject = new ValidateProjectTool(Cli, Fs);
    }

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

internal static class GoldenEvalTasks {
    public const string UnknownActivityXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <ui:NClick DisplayName="Click ok" />
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

    public static async Task<EvalOutcome> ExcelForeach(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "01-excel-foreach",
            name: "Excel ForEachRow",
            specFile: "01-excel-foreach.json",
            relativePath: "Workflows/ExcelForeach.xaml",
            mustContain: ["<ui:ReadRange", "<ui:ForEachRow", "<ui:LogMessage"]);

    public static async Task<EvalOutcome> InvokeArgs(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "02-invoke-args",
            name: "InvokeWorkflowFile + arguments",
            specFile: "02-invoke-args.json",
            relativePath: "Workflows/InvokeChild.xaml",
            mustContain: ["WorkflowFileName=\"Workflows/Child.xaml\"", "InvokeWorkflowFile.Arguments", "x:Key=\"in_Path\"", "x:Key=\"out_Ok\""]);

    public static async Task<EvalOutcome> TryCatchRetry(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "03-trycatch-retry",
            name: "TryCatch + RetryScope",
            specFile: "03-trycatch-retry.json",
            relativePath: "Workflows/RetryTryCatch.xaml",
            mustContain: ["<ui:RetryScope", "<TryCatch>", "<TryCatch.Catches>", "<Catch x:TypeArguments=\"System.Exception\">"]);

    public static async Task<EvalOutcome> IfElse(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "04-if-else",
            name: "If / Else",
            specFile: "04-if-else.json",
            relativePath: "Workflows/IfElse.xaml",
            mustContain: ["<If.Then>", "<If.Else>", "<ui:LogMessage", "<WriteLine"]);

    public static async Task<EvalOutcome> Switch(GoldenEvalContext ctx) =>
        await Author(
            ctx,
            id: "05-switch",
            name: "Switch cases + default",
            specFile: "05-switch.json",
            relativePath: "Workflows/SwitchStatus.xaml",
            mustContain: ["<Switch x:TypeArguments=\"Int32\"", "x:Key=\"1\"", "x:Key=\"2\"", "<Switch.Default>", "<Rethrow"]);

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
            name: "Coded helper (InvokeCode + .cs)",
            specFile: "06-coded-helper.json",
            relativePath: "Workflows/CodedHelper.xaml",
            mustContain: ["<ui:InvokeCode", "InvoiceHelper.Normalize"]);
        return author with {
            Detail = author.Passed
                ? "spec validated, XAML emitted, InvoiceHelper.cs written"
                : author.Detail
        };
    }

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
            mustContain: ["WorkflowFileName=\"Workflows/Child.xaml\"", "x:Key=\"in_Path\""]);
    }

    public static async Task<EvalOutcome> UnknownXamlWriteRefused(GoldenEvalContext ctx) {
        var result = await ctx.Write.WriteWorkflowFile(
            GoldenEvalContext.ProjectPath, "Workflows/UnknownClick.xaml", UnknownActivityXaml);
        var wrote = ctx.Fs.Writes.ContainsKey(GoldenEvalContext.Target("Workflows/UnknownClick.xaml"));
        var refused = result.Status == "error"
            && result.ErrorDetails.Exists(e => e.ErrorCode == ToolErrorCodes.SpecUnknownActivity)
            && !wrote;
        return new EvalOutcome(
            "08-unknown-xaml-refused",
            "Unknown-XAML write refused",
            Passed: refused,
            SpecValidates: null,
            XamlEmits: null,
            UnknownXamlWriteSucceeded: !refused,
            Detail: refused
                ? "write_workflow_file refused NClick (not in catalog)"
                : $"escape hatch opened: status={result.Status} wrote={wrote}");
    }

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
                : $"missing excel read hit or cap violated: [{string.Join(",", names)}]");
    }

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

        var result = await ctx.ValidateProject.ValidateProject(GoldenEvalContext.ProjectPath, validate: true, build: false, pack: false);
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
                : $"unexpected mapping activityId={activityId} property={property}");
    }

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
            await UnknownXamlWriteRefused(ctx),
            await RecommendActivitiesHits(ctx),
            await ValidateProjectDiagnostics(ctx)
        ];
    }

    private static async Task<EvalOutcome> Author(
        GoldenEvalContext ctx,
        string id,
        string name,
        string specFile,
        string relativePath,
        string[] mustContain) {
        var specJson = GoldenEvalContext.LoadSpec(specFile);
        var validated = await ctx.ValidateSpec.ValidateActivitySpec(specJson, GoldenEvalContext.ProjectPath);
        var specOk = validated.Status == "success";
        if (!specOk) {
            var codes = string.Join("; ", validated.ErrorDetails.Select(e => $"{e.ErrorCode}:{e.Message}"));
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"validate_activity_spec failed: {codes}");
        }

        var built = await ctx.Build.BuildWorkflow(GoldenEvalContext.ProjectPath, relativePath, specJson);
        var target = GoldenEvalContext.Target(relativePath);
        if (built.Status != "success" || !ctx.Fs.Writes.TryGetValue(target, out var xaml)) {
            var codes = string.Join("; ", built.ErrorDetails.Select(e => $"{e.ErrorCode}:{e.Message}"));
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"build_workflow failed: {built.Summary} {codes}");
        }

        var missing = mustContain.Where(token => !xaml.Contains(token, StringComparison.Ordinal)).ToList();
        if (missing.Count > 0) {
            return Fail(id, name, specOk, xamlEmits: false, unknownWrite: null,
                $"XAML missing: {string.Join(", ", missing)}");
        }

        return new EvalOutcome(id, name, Passed: true, SpecValidates: true, XamlEmits: true,
            UnknownXamlWriteSucceeded: null, Detail: $"wrote {relativePath}");
    }

    private static EvalOutcome Fail(
        string id, string name, bool? specValidates, bool? xamlEmits, bool? unknownWrite, string detail) =>
        new(id, name, Passed: false, specValidates, xamlEmits, unknownWrite, detail);
}
