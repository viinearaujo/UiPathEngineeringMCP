using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Manages the project's NuGet dependencies through the CLI package verbs. This is the canonical
/// way to add a dependency: hand-editing <c>project.json</c> is wrong and there is no
/// <c>add-dependency</c> verb.
/// </summary>
[McpServerToolType]
public sealed class ManagePackagesTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;

    public ManagePackagesTool(IUiPathCliProvider cli, IFilesystemProvider filesystem, CliCommandPolicy policy) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
    }

    [McpServerTool(UseStructuredContent = true), Description("Manages the project's NuGet dependencies through the UiPath CLI package verbs — the canonical path (uip rpa packages install|versions|inspect). Do NOT hand-edit project.json to add a dependency: there is no add-dependency verb, and patch_project_json(upsert_dependency) only rewrites the JSON without restoring or resolving. operation=install: one packageId (repeat with separate calls for several); omit version to resolve the latest compatible (preferred), pin only for a known constraint — install mutates the project and is blocked unless UiPathCli:EnableMutatingCommands is set. operation=versions: lists available versions, --include-prerelease by default since activity packages frequently ship -preview between stable releases carrying the freshest activity surface and .local/docs. operation=inspect: returns a package's public API as markdown (from a feed or a local .nupkg). Next: validate_project.")]
    public async Task<ToolResult> ManagePackages(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Operation: install, versions, or inspect.")]
        [AllowedValues(Install, Versions, Inspect)] string operation,
        [Description("NuGet package id, e.g. 'UiPath.Excel.Activities'. Required for install and versions; for inspect use packageName instead.")] string? packageId = null,
        [Description("Version to pin for install, e.g. '24.10.3'. Omit to resolve the latest compatible automatically (preferred). Ignored by versions and inspect.")] string? version = null,
        [Description("For versions: include prerelease versions (default true). Activity packages frequently ship -preview carrying the newest activity surface.")] bool includePrerelease = true,
        [Description("For inspect: package name. Not required when nupkgPath is supplied.")] string? packageName = null,
        [Description("For inspect: package version. Not required when nupkgPath is supplied.")] string? packageVersion = null,
        [Description("For inspect: optional NuGet feed URL. Defaults to the UiPath official feed.")] string? feedUrl = null,
        [Description("For inspect: absolute path to a local .nupkg, inspected without downloading. Must be inside Projects:AllowedRoots.")] string? nupkgPath = null,
        [Description("For install: optional path to a JSON file of additional NuGet feed sources ([{\"Url\":\"...\"}]). Must be inside Projects:AllowedRoots.")] string? nugetSourcesConfigPath = null,
        [Description("Optional CLI timeout in seconds (default 300, max 3600). Raise it when a cold NuGet restore is slow.")] int? timeoutSeconds = null,
        [Description("Optional progress sink. The MCP SDK binds this automatically when the client sent a progress token; do not pass it from a caller.")] IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();
        var reporter = CliToolSupport.ProgressFor(progress, $"Running the package {operation} verb.");

        if (ToolArgs.ParseChoice(operation, "operation", Operations, sw, out var parsedOperation) is { } operationError) {
            return operationError;
        }

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } projectFailure) {
            return projectFailure;
        }

        var result = parsedOperation switch {
            Install => await InstallAsync(projectPath, packageId, version, nugetSourcesConfigPath, timeoutSeconds, sw, cancellationToken),
            Versions => await VersionsAsync(projectPath, packageId, includePrerelease, timeoutSeconds, sw, cancellationToken),
            _ => await InspectAsync(projectPath, packageName, packageVersion, feedUrl, nupkgPath, timeoutSeconds, sw, cancellationToken)
        };

        reporter.Step($"'{parsedOperation}' returned.");
        return result;
    }

    private const string Install = "install";
    private const string Versions = "versions";
    private const string Inspect = "inspect";
    private static readonly string[] Operations = [Install, Versions, Inspect];

    private async Task<ToolResult> InstallAsync(
        string projectPath, string? packageId, string? version, string? nugetSourcesConfigPath,
        int? timeoutSeconds, Stopwatch sw, CancellationToken cancellationToken) {
        if (RequirePackageId(packageId, sw) is { } missing) {
            return missing;
        }

        if (nugetSourcesConfigPath is not null
            && ToolResults.GuardAllowedPath(_filesystem, nugetSourcesConfigPath, sw) is { } sourcesFailure) {
            return sourcesFailure;
        }

        var spec = PackagesParser.FormatPackageSpec(packageId!.Trim(), version?.Trim());
        var tokens = CliVerbArguments.WithRpaVerb(
            CliVerbArguments.PackagesInstall(projectPath, [spec], nugetSourcesConfigPath));

        // install mutates the project: gated by EnableMutatingCommands (fail closed).
        if (CliToolSupport.GuardSubcommand(_filesystem, _policy, projectPath, tokens, sw) is { } guardFailure) {
            return guardFailure;
        }

        var outcome = await _cli.RunStructuredAsync(
            CliVerbArguments.RpaVerb, tokens, projectPath,
            timeoutSeconds: CliToolSupport.ClampTimeout(timeoutSeconds), cancellationToken);

        var cli = outcome.Cli;
        if (outcome.Envelope is not { } envelope) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, "validate_project");
        }

        var result = PackagesParser.ParseInstall(envelope.Data, [spec], envelope.Message);
        var success = envelope.IsSuccess && result.Succeeded;

        if (!success) {
            var message = string.Join(" ", new[] { envelope.Message, envelope.Instructions }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            var errors = result.Failed.Count > 0 ? result.Failed : [message.Length > 0 ? message : cli.Summary];
            return ToolResults.Failure(
                errors.Count > 0 ? errors[0] : cli.Summary,
                [new ToolError(
                    ToolErrorCodes.OperationFailed,
                    string.Join("; ", errors),
                    "Package not found: verify the exact id with find_activity or the package's .local/docs. Feed or network error: check the NuGet feed configuration in Studio settings, or pass nugetSourcesConfigPath.",
                    "find_activity")], sw);
        }

        return ToolResults.Ok(
            $"Installed {spec}. Next: validate_project(build:true) to confirm the project still compiles.",
            new {
                operation = Install,
                requested = result.Requested,
                resolvedLatest = string.IsNullOrWhiteSpace(version),
                failed = result.Failed,
                command = cli.Command
            }, sw);
    }

    private async Task<ToolResult> VersionsAsync(
        string projectPath, string? packageId, bool includePrerelease,
        int? timeoutSeconds, Stopwatch sw, CancellationToken cancellationToken) {
        if (RequirePackageId(packageId, sw) is { } missing) {
            return missing;
        }

        var tokens = CliVerbArguments.WithRpaVerb(
            CliVerbArguments.PackagesVersions(projectPath, packageId!.Trim(), includePrerelease));

        // versions is read-only and listed in UiPathCli:ReadOnlySubcommands.
        if (CliToolSupport.GuardSubcommand(_filesystem, _policy, projectPath, tokens, sw) is { } guardFailure) {
            return guardFailure;
        }

        var outcome = await _cli.RunStructuredAsync(
            CliVerbArguments.RpaVerb, tokens, projectPath,
            timeoutSeconds: CliToolSupport.ClampTimeout(timeoutSeconds), cancellationToken);

        var cli = outcome.Cli;
        if (outcome.Envelope is not { } envelope) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, "manage_packages");
        }

        if (!envelope.IsSuccess) {
            return CliToolSupport.EnvelopeFailure(envelope, cli.Summary, sw, "manage_packages",
                "Verify the exact package id with find_activity. A feed or network error means the NuGet feed configuration in Studio settings needs attention.");
        }

        var versions = PackagesParser.ParseVersions(envelope.Data);
        var warnings = new List<string>();
        if (versions.Versions.Count == 0) {
            warnings.Add($"No versions were reported for '{packageId}'. Verify the exact package id with find_activity, or pass includePrerelease=true.");
        }

        return ToolResults.Ok(
            $"{versions.Versions.Count} version(s) available for {packageId}.",
            new {
                operation = Versions,
                packageId = versions.PackageId,
                includePrerelease = versions.IncludePrerelease,
                latest = versions.Latest,
                latestStable = versions.LatestStable,
                versions = versions.Versions,
                command = cli.Command,
                note = "Omit version on manage_packages(install) to resolve the latest compatible automatically."
            }, sw, warnings);
    }

    private async Task<ToolResult> InspectAsync(
        string projectPath, string? packageName, string? packageVersion, string? feedUrl, string? nupkgPath,
        int? timeoutSeconds, Stopwatch sw, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(packageName) && string.IsNullOrWhiteSpace(nupkgPath)) {
            return ToolResults.Failure(new ToolError(
                CliToolErrorCodes.InvalidArgument,
                "operation=inspect needs packageName (plus an optional packageVersion) or nupkgPath.",
                "Pass packageName='UiPath.Excel.Activities', or nupkgPath to inspect a local .nupkg without downloading."), sw);
        }

        if (nupkgPath is not null
            && ToolResults.GuardAllowedPath(_filesystem, nupkgPath, sw) is { } nupkgFailure) {
            return nupkgFailure;
        }

        var tokens = CliVerbArguments.WithRpaVerb(
            CliVerbArguments.PackagesInspect(projectPath, packageName, packageVersion, feedUrl, nupkgPath));

        // inspect is read-only and listed in UiPathCli:ReadOnlySubcommands.
        if (CliToolSupport.GuardSubcommand(_filesystem, _policy, projectPath, tokens, sw) is { } guardFailure) {
            return guardFailure;
        }

        var outcome = await _cli.RunStructuredAsync(
            CliVerbArguments.RpaVerb, tokens, projectPath,
            timeoutSeconds: CliToolSupport.ClampTimeout(timeoutSeconds), cancellationToken);

        var cli = outcome.Cli;
        if (outcome.Envelope is not { } envelope) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, "manage_packages");
        }

        if (!envelope.IsSuccess) {
            return CliToolSupport.EnvelopeFailure(envelope, cli.Summary, sw, "manage_packages",
                "Package not found on the feed: verify the exact id and version, or pass nupkgPath to inspect a local package directly.");
        }

        var documentation = PackagesParser.ReadMarkdown(envelope.Data) ?? cli.StdOut;

        return ToolResults.Ok(
            $"Inspected {(string.IsNullOrWhiteSpace(packageName) ? nupkgPath : packageName)}.",
            new {
                operation = Inspect,
                packageName,
                packageVersion,
                nupkgPath,
                command = cli.Command,
                documentation
            }, sw);
    }

    private static ToolResult? RequirePackageId(string? packageId, Stopwatch sw) =>
        string.IsNullOrWhiteSpace(packageId)
            ? ToolResults.Failure(new ToolError(
                CliToolErrorCodes.InvalidArgument,
                "packageId is required for operation=install and operation=versions.",
                "Pass packageId='UiPath.Excel.Activities'. For operation=inspect use packageName instead."), sw)
            : null;
}
