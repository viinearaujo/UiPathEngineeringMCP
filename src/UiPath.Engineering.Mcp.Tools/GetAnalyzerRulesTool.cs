using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Jobs;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Lists the Workflow Analyzer rules enabled for a project via
/// <c>uip rpa analyzer-rules list</c>. Scoped by default: the unscoped call enumerates every rule
/// across every package and can take a minute or more.
/// </summary>
[McpServerToolType]
public sealed class GetAnalyzerRulesTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;
    private readonly IBackgroundJobStore _jobs;

    public GetAnalyzerRulesTool(
        IUiPathCliProvider cli,
        IFilesystemProvider filesystem,
        CliCommandPolicy policy,
        IBackgroundJobStore jobs) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
        _jobs = jobs;
    }

    [McpServerTool(
        UseStructuredContent = true,
        Title = "Get Analyzer Rules",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true),
     Description("Leave-off. Starts analyzer-rules list as a background job; returns {jobId,status:running}. Always pass scope. Next: get_job.")]
    public Task<ToolResult> GetAnalyzerRules(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Scope filter: Activity, Workflow, Project, or Coded Workflow. Defaults to Workflow. Pass 'All' only deliberately — the unscoped call enumerates every installed package's rules and can take a minute or more.")]
        [AllowedValues(CliVerbArguments.AnalyzerScopeActivity, CliVerbArguments.AnalyzerScopeWorkflow, CliVerbArguments.AnalyzerScopeProject, CliVerbArguments.AnalyzerScopeCodedWorkflow, AllScope)] string scope = CliVerbArguments.AnalyzerScopeWorkflow,
        [Description("Optional minimum severity to return: error, warning, or info (default info, i.e. no filtering).")]
        [AllowedValues(SeverityError, SeverityWarning, SeverityInfo)] string? minSeverity = null,
        [Description("Optional CLI timeout in seconds (default 300, max 3600). Raise it for an unscoped call on a large package set.")] int? timeoutSeconds = null,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();
        if (ToolArgs.ParseChoice(scope, "scope", AnalyzerScopes, sw, out var parsedScope) is { } scopeError) {
            return Task.FromResult(scopeError);
        }

        if (ToolArgs.ParseChoice(minSeverity ?? LowestSeverity, "minSeverity", Severities, sw, out var parsedSeverity) is { } severityError) {
            return Task.FromResult(severityError);
        }

        var scoped = !string.Equals(parsedScope, AllScope, StringComparison.OrdinalIgnoreCase);
        var tokens = CliVerbArguments.WithRpaVerb(scoped
            ? CliVerbArguments.AnalyzerRulesList(projectPath, parsedScope!)
            : CliVerbArguments.AnalyzerRulesUnscoped(projectPath));

        if (CliToolSupport.GuardSubcommand(_filesystem, _policy, projectPath, tokens, sw) is { } guardFailure) {
            return Task.FromResult(guardFailure);
        }

        _ = cancellationToken;
        var timeout = CliToolSupport.ClampTimeout(timeoutSeconds);
        return Task.FromResult(CliToolSupport.StartBackgroundJob(
            _jobs,
            "get_analyzer_rules",
            async (jobProgress, jobCt) => {
                jobProgress.Report("Reading Workflow Analyzer rules.");
                var outcome = await _cli.RunStructuredAsync(
                    CliVerbArguments.RpaVerb, tokens, projectPath,
                    timeoutSeconds: timeout, jobCt);

                var cli = outcome.Cli;
                if (outcome.Envelope is not { } envelope) {
                    return CliToolSupport.CliFailure(outcome, cli.Summary, Stopwatch.StartNew(), SuggestedTool);
                }

                if (!envelope.IsSuccess) {
                    return CliToolSupport.EnvelopeFailure(envelope, cli.Summary, Stopwatch.StartNew(), SuggestedTool,
                        "Confirm the path points at the project.json folder and the project opens cleanly, then retry once. A cold headless Studio start can take 30-90s.");
                }

                var rules = AnalyzerRulesParser.ParseData(envelope.Data);
                jobProgress.Report($"Returned {rules.Count} rule(s).");
                var filtered = FilterBySeverity(rules, parsedSeverity!);
                var warnings = new List<string>();

                if (rules.Count == 0) {
                    warnings.Add(scoped
                        ? $"No analyzer rules were reported for scope '{parsedScope}'. Try another scope, or pass scope=All (slow — it enumerates every installed package)."
                        : "No analyzer rules were reported. The project may carry no analyzer configuration, or the CLI returned an unrecognized payload.");
                }

                if (rules.Count > filtered.Count) {
                    warnings.Add($"{rules.Count - filtered.Count} rule(s) below severity '{parsedSeverity}' were filtered out.");
                }

                if (!scoped && rules.Count > 0) {
                    warnings.Add("The unscoped call enumerates every rule across every installed package and can take a minute or more. Pass scope to narrow the next call.");
                }

                return ToolResults.Ok(
                    $"{filtered.Count} enabled analyzer rule(s) returned (scope: {parsedScope}, minSeverity: {parsedSeverity}).",
                    new {
                        scope = parsedScope,
                        minSeverity = parsedSeverity,
                        totalRules = rules.Count,
                        returnedRules = filtered.Count,
                        command = cli.Command,
                        rules = filtered.Select(r => new {
                            id = r.Id,
                            severity = r.Severity,
                            scope = r.Scope,
                            title = r.Title,
                            recommendation = r.Recommendation,
                            docs = r.Docs,
                            parameters = r.Parameters,
                            source = r.Source
                        }),
                        note = "These are the ENABLED rules, not violations. validate_project reports violations carrying the same rule ids."
                    }, Stopwatch.StartNew(), warnings);
            },
            progress,
            sw));
    }

    private const string SuggestedTool = "validate_project";
    private const string DefaultScope = CliVerbArguments.AnalyzerScopeWorkflow;
    private const string AllScope = "All";
    private const string LowestSeverity = SeverityInfo;

    private const string SeverityError = "error";
    private const string SeverityWarning = "warning";
    private const string SeverityInfo = "info";

    private static readonly string[] AnalyzerScopes = [.. CliVerbArguments.AnalyzerRuleScopes, AllScope];
    private static readonly string[] Severities = [SeverityError, SeverityWarning, LowestSeverity];

    private static IReadOnlyList<AnalyzerRule> FilterBySeverity(IReadOnlyList<AnalyzerRule> rules, string minSeverity) {
        var minimum = SeverityRank(minSeverity);
        return rules.Where(r => SeverityRank(r.Severity) <= minimum).ToList();
    }

    // Lower rank = more severe, so "<= minimum" keeps that severity and anything worse.
    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch {
        "error" => 0,
        "warning" => 1,
        _ => 2
    };
}
