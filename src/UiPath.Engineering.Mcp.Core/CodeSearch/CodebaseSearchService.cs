using Microsoft.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.CodeSearch;

/// <summary>
/// Substring search over a UiPath project's .xaml and .cs files. Text mode scans
/// file contents line-by-line; symbol mode reuses SP1's cached compilation;
/// activity/workflow modes reuse the cached UiPathProjectModel. Stateless beyond
/// the injected caches; every method takes the project path.
/// </summary>
public sealed class CodebaseSearchService : ICodebaseSearchService {
    // ~2 MB of text: files larger than this are skipped rather than scanned. The pre-read
    // check compares bytes (GetFileSize) so oversized files are never loaded into memory.
    internal const int MaxFileCharacters = 2_000_000;
    private const int MaxSnippetLength = 300;

    private readonly ICSharpContextBuilder _contextBuilder;
    private readonly IProjectModelBuilder _projectModelBuilder;
    private readonly IFilesystemProvider _filesystem;

    public CodebaseSearchService(
        ICSharpContextBuilder contextBuilder,
        IProjectModelBuilder projectModelBuilder,
        IFilesystemProvider filesystem) {
        _contextBuilder = contextBuilder;
        _projectModelBuilder = projectModelBuilder;
        _filesystem = filesystem;
    }

    public Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        var result = new TextSearchResult();
        var matches = new List<(TextMatch Match, bool Exact)>();

        var files = _filesystem.FindXamlFiles(projectPath).Concat(_filesystem.FindCSharpFiles(projectPath));
        foreach (var file in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try {
                var fileSize = _filesystem.GetFileSize(file);
                if (fileSize > MaxFileCharacters) {
                    result.SkippedFiles.Add(file);
                    result.Warnings.Add($"Skipped oversized file '{file}' ({fileSize} bytes).");
                    continue;
                }
                content = _filesystem.ReadAllText(file);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped unreadable file '{file}': {ex.Message}");
                continue;
            }
            // Safety net: GetFileSize reports bytes; keep the char-accurate check post-read.
            if (content.Length > MaxFileCharacters) {
                result.SkippedFiles.Add(file);
                result.Warnings.Add($"Skipped oversized file '{file}' ({content.Length} characters).");
                continue;
            }
            result.FilesSearched++;

            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++) {
                var exact = lines[i].Contains(query, StringComparison.Ordinal);
                if (!exact && !lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                matches.Add((new TextMatch { FilePath = file, Line = i + 1, Snippet = TrimSnippet(lines[i]) }, exact));
            }
        }

        foreach (var (match, _) in matches
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Match.Line)
            .Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            result.Note = $"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.";
        }
        return Task.FromResult(result);
    }

    public async Task<SymbolSearchResult> SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken cancellationToken = default) {
        var context = await _contextBuilder.BuildAsync(projectPath, cancellationToken);
        var result = new SymbolSearchResult {
            AnalysisMode = context.Mode switch {
                CSharpAnalysisMode.Full => "full",
                CSharpAnalysisMode.Partial => "partial",
                _ => "syntaxOnly"
            },
            UnresolvedReferences = [.. context.UnresolvedReferences],
            Warnings = [.. context.Warnings],
            HasCSharpFiles = context.HasCSharpFiles
        };
        if (!context.HasCSharpFiles) {
            result.Note = "The project contains no C# files.";
            return result;
        }

        // GetSymbolsWithName only does exact-name lookup, so substring search
        // enumerates source symbols from the global namespace instead.
        var matches = EnumerateSourceSymbols(context.Compilation.GlobalNamespace)
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(s => CSharpAnalysisService.KindMatches(s, kind))
            .Select(s => (Match: CSharpAnalysisService.ToSymbolMatch(s), Exact: string.Equals(s.Name, query, StringComparison.Ordinal)))
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Match.Line)
            .ThenBy(m => m.Match.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var (match, _) in matches.Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            result.Note = $"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.";
        }
        return result;
    }

    public async Task<ActivitySearchResult> SearchActivitiesAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        var model = await _projectModelBuilder.BuildAsync(projectPath, cancellationToken);
        var result = new ActivitySearchResult();
        var parseErrors = model.Workflows.Count(w => w.HasParseError);
        var matches = new List<(ActivityMatch Match, bool Exact)>();

        foreach (var workflow in model.Workflows.Where(w => !w.HasParseError)) {
            result.WorkflowsSearched++;
            foreach (var activity in workflow.Activities) {
                cancellationToken.ThrowIfCancellationRequested();
                var nameHit = activity.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase);
                var typeHit = activity.Type.Contains(query, StringComparison.OrdinalIgnoreCase);
                if (!nameHit && !typeHit) {
                    continue;
                }
                var exact = string.Equals(activity.DisplayName, query, StringComparison.Ordinal)
                    || string.Equals(activity.Type, query, StringComparison.Ordinal);
                matches.Add((new ActivityMatch {
                    WorkflowFile = workflow.FileName,
                    WorkflowPath = workflow.FilePath,
                    DisplayName = activity.DisplayName,
                    ActivityType = activity.Type,
                    Depth = activity.Depth
                }, exact));
            }
        }

        // OrderBy is stable: within the same workflow, activities keep document order.
        foreach (var (match, _) in matches
            .OrderBy(m => m.Exact ? 0 : 1)
            .ThenBy(m => m.Match.WorkflowPath, StringComparer.OrdinalIgnoreCase)
            .Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }

        var notes = new List<string>();
        if (parseErrors > 0) {
            notes.Add($"{parseErrors} workflow(s) failed to parse and were skipped.");
        }
        if (result.Matches.Count > 0) {
            notes.Add("Activity hits locate the workflow file; line-level activity addressing lands with SP3.");
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            notes.Add($"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.");
        }
        result.Note = notes.Count > 0 ? string.Join(' ', notes) : null;
        return result;
    }

    public async Task<WorkflowSearchResult> SearchWorkflowsAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
        var model = await _projectModelBuilder.BuildAsync(projectPath, cancellationToken);
        var result = new WorkflowSearchResult();

        var matches = model.Workflows
            .Select(w => {
                var nameHit = w.FileName.Contains(query, StringComparison.OrdinalIgnoreCase);
                var descriptionHit = w.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false;
                return (Workflow: w, NameHit: nameHit, DescriptionHit: descriptionHit);
            })
            .Where(x => x.NameHit || x.DescriptionHit)
            .Select(x => (Match: new WorkflowMatch {
                FileName = x.Workflow.FileName,
                FilePath = x.Workflow.FilePath,
                IsMain = x.Workflow.IsMain,
                Description = x.Workflow.Description,
                MatchedOn = x.NameHit && x.DescriptionHit ? "both" : x.NameHit ? "name" : "description"
            }, Exact: string.Equals(x.Workflow.FileName, query, StringComparison.Ordinal)
                || string.Equals(Path.GetFileNameWithoutExtension(x.Workflow.FileName), query, StringComparison.Ordinal)))
            .OrderBy(x => x.Exact ? 0 : 1)
            .ThenBy(x => x.Match.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var (match, _) in matches.Take(CSharpAnalysisService.MaxResults)) {
            result.Matches.Add(match);
        }

        var notes = new List<string>();
        var parseErrors = model.Workflows.Count(w => w.HasParseError);
        if (parseErrors > 0) {
            notes.Add($"{parseErrors} workflow(s) failed to parse.");
        }
        if (matches.Count > CSharpAnalysisService.MaxResults) {
            result.Truncated = true;
            notes.Add($"Results truncated at {CSharpAnalysisService.MaxResults} matches; narrow the query.");
        }
        result.Note = notes.Count > 0 ? string.Join(' ', notes) : null;
        return result;
    }

    // Yields source-declared named types (recursing into their members), methods,
    // properties, and fields. Metadata symbols and implicit members are excluded,
    // as are accessor methods (get_*/set_*), which surface via their property/event.
    private static IEnumerable<ISymbol> EnumerateSourceSymbols(INamespaceOrTypeSymbol container) {
        foreach (var member in container.GetMembers()) {
            if (member is INamespaceSymbol ns) {
                foreach (var nested in EnumerateSourceSymbols(ns)) {
                    yield return nested;
                }
                continue;
            }

            if (member.IsImplicitlyDeclared || !member.Locations.Any(l => l.IsInSource) ||
                member is IMethodSymbol { AssociatedSymbol: not null }) {
                continue;
            }
            if (member is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol) {
                yield return member;
            }
            if (member is INamedTypeSymbol type) {
                foreach (var nested in EnumerateSourceSymbols(type)) {
                    yield return nested;
                }
            }
        }
    }

    private static string TrimSnippet(string line) {
        var trimmed = line.Trim();
        return trimmed.Length <= MaxSnippetLength ? trimmed : trimmed[..MaxSnippetLength] + "…";
    }
}
