using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools;

/// <summary>
/// Reads the project's Object Repository (apps → screens → elements) via the CLI read verbs, so
/// a caller reuses existing targets instead of emitting placeholder selectors with TODO Indicate
/// markers. Read-only: it never writes the repository.
/// </summary>
[McpServerToolType]
public sealed class GetObjectRepositoryTool {
    private readonly IUiPathCliProvider _cli;
    private readonly IFilesystemProvider _filesystem;
    private readonly CliCommandPolicy _policy;

    public GetObjectRepositoryTool(IUiPathCliProvider cli, IFilesystemProvider filesystem, CliCommandPolicy policy) {
        _cli = cli;
        _filesystem = filesystem;
        _policy = policy;
    }

    [McpServerTool(UseStructuredContent = true), Description("Reads a UiPath project's Object Repository — the saved hierarchy of applications → screens → elements (selectors/targets) that UI Automation activities bind to — via the CLI read verbs (uip rpa get-object-repository / get-library-object-repository). Read this BEFORE authoring UI Automation activities so you reuse an existing screen/element by name+reference instead of emitting a placeholder selector with a TODO Indicate marker. source=project (default) returns the project's own entries; entries inherited from referenced libraries are excluded. source=library reads the Object Repository out of one or more library .nupkg files (pass libraryPaths), grouped by library. Requires an open project (Studio IPC). Each node carries name, type (App/Screen/Element), taxonomyType, reference, and a dotted path. Next: find_activity.")]
    public async Task<ToolResult> GetObjectRepository(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Which repository to read: project (the project's own entries) or library (entries exposed by referenced library .nupkg files).")] string source = "project",
        [Description("For source=library: absolute path(s) to the library .nupkg file(s). Passed as ONE comma-separated value; avoid paths containing commas. Must be inside Projects:AllowedRoots.")] string? libraryPaths = null,
        [Description("Optional case-insensitive substring to filter nodes by name (matches apps, screens, and elements).")] string? query = null,
        [Description("Optional maximum tree depth to return (0 = unlimited, default). Set to 1 for just apps, 2 for apps+screens.")] int? maxDepth = null,
        [Description("Optional CLI timeout in seconds (default 300, max 3600).")] int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolArgs.ParseChoice(source, "source", Sources, sw, out var parsedSource) is { } sourceError) {
            return sourceError;
        }

        var isLibrary = string.Equals(parsedSource, LibrarySource, StringComparison.OrdinalIgnoreCase);

        if (isLibrary) {
            if (string.IsNullOrWhiteSpace(libraryPaths)) {
                return ToolResults.Failure(new ToolError(
                    CliToolErrorCodes.InvalidArgument,
                    "source=library requires libraryPaths.",
                    "Pass one or more absolute .nupkg paths as a single comma-separated value, e.g. 'C:\\libs\\Acme.UiLib.1.2.0.nupkg'."), sw);
            }

            foreach (var path in libraryPaths!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                if (ToolResults.GuardAllowedPath(_filesystem, path, sw) is { } pathFailure) {
                    return pathFailure;
                }
            }
        }

        var tokens = CliVerbArguments.WithRpaVerb(isLibrary
            ? CliVerbArguments.ObjectRepositoryGetLibrary(projectPath, libraryPaths!)
            : CliVerbArguments.ObjectRepositoryGet(projectPath));

        // Both OR read verbs are read-only and listed in UiPathCli:ReadOnlySubcommands.
        if (CliToolSupport.GuardSubcommand(_filesystem, _policy, projectPath, tokens, sw) is { } guardFailure) {
            return guardFailure;
        }

        var outcome = await _cli.RunStructuredAsync(
            CliVerbArguments.RpaVerb, tokens, projectPath,
            timeoutSeconds: CliToolSupport.ClampTimeout(timeoutSeconds), cancellationToken);

        var cli = outcome.Cli;
        if (outcome.Envelope is not { } envelope) {
            return CliToolSupport.CliFailure(outcome, cli.Summary, sw, SuggestedTool);
        }

        if (!envelope.IsSuccess) {
            return CliToolSupport.EnvelopeFailure(envelope, cli.Summary, sw, SuggestedTool,
                "Both Object Repository read verbs require an open project over Studio IPC. Confirm the path points at the project.json folder and the project opens cleanly, then retry once.");
        }

        if (isLibrary) {
            return LibraryPayload(ObjectRepositoryParser.ParseLibraries(envelope.Data), cli, query, maxDepth, sw);
        }

        var project = ObjectRepositoryParser.ParseProject(envelope.Data);
        return ProjectPayload(project, cli, query, maxDepth, sw);
    }

    private const string SuggestedTool = "find_activity";
    private const string ProjectSource = "project";
    private const string LibrarySource = "library";
    private static readonly string[] Sources = [ProjectSource, LibrarySource];

    private static ToolResult ProjectPayload(
        ObjectRepositoryResult repository, UiPathCliResult cli, string? query, int? maxDepth, Stopwatch sw) {
        var warnings = new List<string>();
        if (repository.Flat.Count == 0) {
            warnings.Add("The project Object Repository is empty. Capture screens/elements in Studio first, or read a referenced library's repository with source=library.");
        }

        var tree = ShapeTree(repository.Nodes, query, maxDepth);
        var matched = CountMatched(repository.Nodes, query, maxDepth);

        return ToolResults.Ok(
            $"Object Repository: {repository.Apps} app(s), {repository.Screens} screen(s), {repository.Elements} element(s).",
            new {
                source = ProjectSource,
                apps = repository.Apps,
                screens = repository.Screens,
                elements = repository.Elements,
                total = repository.Flat.Count,
                returned = matched,
                command = cli.Command,
                tree,
                note = "Reuse an existing node's name+reference when authoring UI Automation activities; do not emit a placeholder selector with a TODO Indicate marker for a target that already exists here."
            }, sw, warnings);
    }

    private static ToolResult LibraryPayload(
        IReadOnlyList<ObjectRepositoryResult> libraries, UiPathCliResult cli, string? query, int? maxDepth, Stopwatch sw) {
        var warnings = new List<string>();
        if (libraries.Count == 0) {
            warnings.Add("No library carried an Object Repository. Packages without one are omitted from the result; verify the .nupkg paths.");
        }

        return ToolResults.Ok(
            $"Library Object Repository: {libraries.Count} library/libraries read.",
            new {
                source = LibrarySource,
                command = cli.Command,
                libraries = libraries.Select(l => new {
                    library = l.Library,
                    apps = l.Apps,
                    screens = l.Screens,
                    elements = l.Elements,
                    total = l.Flat.Count,
                    tree = ShapeTree(l.Nodes, query, maxDepth)
                }),
                note = "These are targets a referenced UI library already exposes; bind UI Automation activities to them by name+reference."
            }, sw, warnings);
    }

    // Shapes the node tree for the response, applying the optional name filter and depth cap.
    // maxDepth counts node levels: 1 keeps apps only, 2 keeps apps and screens (node.Depth is
    // 0-based), 0 or null is unlimited. A node that does not match the query is still emitted when
    // a descendant matches, so the tree stays navigable.
    private static List<object> ShapeTree(IReadOnlyList<ObjectRepositoryNode> nodes, string? query, int? maxDepth) {
        var limit = maxDepth is { } d && d > 0 ? d : int.MaxValue;

        List<object> Walk(IReadOnlyList<ObjectRepositoryNode> items) {
            var shaped = new List<object>();
            foreach (var node in items) {
                var children = node.Depth + 1 < limit ? Walk(node.Children) : [];
                if (!Matches(node, query) && children.Count == 0) {
                    continue;
                }

                shaped.Add(new {
                    name = node.Name,
                    type = node.Type,
                    taxonomyType = node.TaxonomyType,
                    description = node.Description,
                    reference = node.Reference,
                    path = node.Path,
                    depth = node.Depth,
                    children
                });
            }

            return shaped;
        }

        return Walk(nodes);
    }

    private static bool Matches(ObjectRepositoryNode node, string? query) =>
        string.IsNullOrWhiteSpace(query)
        || node.Name.Contains(query!, StringComparison.OrdinalIgnoreCase)
        || (node.TaxonomyType?.Contains(query!, StringComparison.OrdinalIgnoreCase) ?? false);

    private static int CountMatched(IReadOnlyList<ObjectRepositoryNode> nodes, string? query, int? maxDepth) {
        var limit = maxDepth is { } d && d > 0 ? d : int.MaxValue;
        var count = 0;

        void Walk(IReadOnlyList<ObjectRepositoryNode> items) {
            foreach (var node in items) {
                if (Matches(node, query)) {
                    count++;
                }

                if (node.Depth + 1 < limit) {
                    Walk(node.Children);
                }
            }
        }

        Walk(nodes);
        return count;
    }
}
