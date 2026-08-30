using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class ProjectDocsValidator {
    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectKnowledgeStore _knowledge;
    private readonly ProjectAdrStore _adrs;

    public ProjectDocsValidator(
        IFilesystemProvider filesystem,
        ProjectKnowledgeStore knowledge,
        ProjectAdrStore adrs) {
        _filesystem = filesystem;
        _knowledge = knowledge;
        _adrs = adrs;
    }

    public List<DocsFinding> Validate(string projectPath, UiPathProjectModel model) {
        var findings = new List<DocsFinding>();
        AddGeneratedContextFindings(projectPath, model, findings);
        AddKnowledgeFindings(projectPath, findings);
        AddAdrFindings(projectPath, findings);
        AddCoverageWarnings(projectPath, model, findings);
        return findings;
    }

    public static IReadOnlyList<DocsFinding> ErrorFindings(IReadOnlyList<DocsFinding> findings) =>
        findings.Where(f => f.Severity == DocsFinding.Error).ToList();

    private void AddGeneratedContextFindings(string projectPath, UiPathProjectModel model, List<DocsFinding> findings) {
        var (cs, xaml, deps) = ProjectContextRenderer.Counts(model);
        var agentsPath = ProjectDocsPaths.AgentsMd(projectPath);
        var contextPath = ProjectDocsPaths.ProjectContext(projectPath);
        var agentsExists = _filesystem.FileExists(agentsPath);
        var contextExists = _filesystem.FileExists(contextPath);
        var agentsContent = agentsExists ? _filesystem.ReadAllText(agentsPath) : null;
        var hasMarkers = ProjectContextRenderer.HasMarkers(agentsContent);

        if (!hasMarkers && !contextExists) {
            findings.Add(new DocsFinding {
                Code = ToolErrorCodes.DocsStale,
                Severity = DocsFinding.Error,
                Message = "Generated project context is missing (no AGENTS.md markers and no .claude/rules/project-context.md).",
                TargetFile = ProjectDocsPaths.ProjectContextRelativePath,
                SuggestedTool = "sync_project_context",
                FixHint = "Call sync_project_context to generate AGENTS.md and .claude/rules/project-context.md."
            });
            return;
        }

        CheckMetadata(agentsPath, hasMarkers ? agentsContent : null, cs, xaml, deps, findings);
        if (contextExists) {
            CheckMetadata(contextPath, _filesystem.ReadAllText(contextPath), cs, xaml, deps, findings);
        }
    }

    private static void CheckMetadata(string path, string? content, int cs, int xaml, int deps, List<DocsFinding> findings) {
        if (string.IsNullOrEmpty(content)) {
            return;
        }

        if (!ProjectContextRenderer.TryParseMetadata(content, out var foundCs, out var foundXaml, out var foundDeps)) {
            findings.Add(new DocsFinding {
                Code = ToolErrorCodes.DocsStale,
                Severity = DocsFinding.Error,
                Message = $"Generated context in '{path}' is missing the discovery-metadata comment.",
                TargetFile = path,
                SuggestedTool = "sync_project_context",
                FixHint = "Call sync_project_context to regenerate project memory."
            });
            return;
        }

        if (foundCs != cs || foundXaml != xaml || foundDeps != deps) {
            findings.Add(new DocsFinding {
                Code = ToolErrorCodes.DocsStale,
                Severity = DocsFinding.Error,
                Message = $"Generated context in '{path}' is stale (cs={foundCs} xaml={foundXaml} deps={foundDeps}; current cs={cs} xaml={xaml} deps={deps}).",
                TargetFile = path,
                SuggestedTool = "sync_project_context",
                FixHint = "Call sync_project_context to regenerate project memory."
            });
        }
    }

    private void AddKnowledgeFindings(string projectPath, List<DocsFinding> findings) {
        var index = _knowledge.Load(projectPath);
        var markdownFiles = _knowledge.ListMarkdownFiles(projectPath);
        var indexedNames = new HashSet<string>(
            index.Articles.Select(a => a.FileName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var article in index.Articles) {
            var mdPath = ProjectDocsPaths.KnowledgeArticle(projectPath, article.Id);
            if (!_filesystem.FileExists(mdPath)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Error,
                    Message = $"Knowledge index lists '{article.Id}' but '{article.FileName}' is missing.",
                    TargetFile = article.FileName,
                    SuggestedTool = "manage_project_docs",
                    FixHint = "Call manage_project_docs action=write kind=memory to restore the article, or action=delete to drop the index row."
                });
            }

            AddRelatedFileFindings(projectPath, article.RelatedFiles, article.UpdatedUtc, article.Id, "manage_project_docs", "action=write kind=memory", findings);
        }

        foreach (var file in markdownFiles) {
            var name = Path.GetFileName(file);
            if (!indexedNames.Contains(name)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Error,
                    Message = $"Unindexed knowledge markdown '{name}'.",
                    TargetFile = name,
                    SuggestedTool = "manage_project_docs",
                    FixHint = "Call manage_project_docs action=write kind=memory to index it, or action=delete to remove it."
                });
            }
        }
    }

    private void AddAdrFindings(string projectPath, List<DocsFinding> findings) {
        var index = _adrs.Load(projectPath);
        var markdownFiles = _adrs.ListMarkdownFiles(projectPath);
        var indexedNames = new HashSet<string>(
            index.Adrs.Select(a => a.FileName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var adr in index.Adrs) {
            var mdPath = ProjectDocsPaths.AdrFile(projectPath, adr.FileName);
            if (!_filesystem.FileExists(mdPath)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Error,
                    Message = $"ADR index lists '{adr.Id}' but '{adr.FileName}' is missing.",
                    TargetFile = adr.FileName,
                    SuggestedTool = "manage_project_docs",
                    FixHint = "Call manage_project_docs action=write kind=adr to restore the ADR, or action=delete to drop the index row."
                });
            } else {
                var content = _filesystem.ReadAllText(mdPath);
                if (!ProjectAdrStore.HasRequiredSections(content)) {
                    findings.Add(new DocsFinding {
                        Code = ToolErrorCodes.DocsAdrIncomplete,
                        Severity = DocsFinding.Error,
                        Message = $"ADR '{adr.Id}' is missing required sections (Context, Decision, Consequences).",
                        TargetFile = adr.FileName,
                        SuggestedTool = "manage_project_docs",
                        FixHint = "Call manage_project_docs action=write kind=adr with a Nygard template that includes Context, Decision, and Consequences."
                    });
                }
            }

            AddRelatedFileFindings(projectPath, adr.RelatedFiles, adr.UpdatedUtc, adr.Id, "manage_project_docs", "action=write kind=adr", findings);

            if (string.Equals(adr.Status, AdrRecord.Proposed, StringComparison.OrdinalIgnoreCase)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Warning,
                    Message = $"ADR '{adr.Id}' is still proposed.",
                    TargetFile = adr.FileName,
                    SuggestedTool = "manage_project_docs",
                    FixHint = "Call manage_project_docs action=write kind=adr to accept or supersede it when the decision is final."
                });
            }
        }

        foreach (var file in markdownFiles) {
            var name = Path.GetFileName(file);
            if (!indexedNames.Contains(name)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Error,
                    Message = $"Unindexed ADR markdown '{name}'.",
                    TargetFile = name,
                    SuggestedTool = "manage_project_docs",
                    FixHint = "Call manage_project_docs action=write kind=adr to index it, or action=delete to remove it."
                });
            }
        }
    }

    private void AddRelatedFileFindings(
        string projectPath,
        IReadOnlyList<string> relatedFiles,
        DateTimeOffset updatedUtc,
        string ownerId,
        string suggestedTool,
        string actionHint,
        List<DocsFinding> findings) {

        foreach (var related in relatedFiles) {
            var target = ProjectFilePolicy.CombineProject(projectPath, related);
            if (!ProjectFilePolicy.IsWithinProject(projectPath, target) || !_filesystem.FileExists(target)) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsInconsistent,
                    Severity = DocsFinding.Error,
                    Message = $"'{ownerId}' relatedFiles entry '{related}' does not exist.",
                    TargetFile = related,
                    SuggestedTool = suggestedTool,
                    FixHint = $"Call {suggestedTool} {actionHint} to update or delete the doc after the file was removed."
                });
                continue;
            }

            DateTime mtime;
            try {
                mtime = _filesystem.GetLastWriteTimeUtc(target);
            } catch (FileNotFoundException) {
                continue;
            }

            if (mtime > updatedUtc.UtcDateTime) {
                findings.Add(new DocsFinding {
                    Code = ToolErrorCodes.DocsStale,
                    Severity = DocsFinding.Error,
                    Message = $"'{ownerId}' is stale: '{related}' was modified after the doc (updatedUtc {updatedUtc:O}).",
                    TargetFile = related,
                    SuggestedTool = suggestedTool,
                    FixHint = $"Call {suggestedTool} {actionHint} to refresh the article after the code change."
                });
            }
        }
    }

    private void AddCoverageWarnings(string projectPath, UiPathProjectModel model, List<DocsFinding> findings) {
        var related = _knowledge.Load(projectPath).Articles.SelectMany(a => a.RelatedFiles)
            .Concat(_adrs.Load(projectPath).Adrs.SelectMany(a => a.RelatedFiles))
            .Select(ProjectFilePolicy.NormalizeRelativePath)
            .ToList();

        foreach (var fileName in model.Workflows.Select(w => w.FileName).Concat(model.CodedWorkflows.Select(c => c.FileName))) {
            if (string.IsNullOrWhiteSpace(fileName)) {
                continue;
            }

            var covered = related.Any(r =>
                string.Equals(r, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(r), fileName, StringComparison.OrdinalIgnoreCase));
            if (covered) {
                continue;
            }

            findings.Add(new DocsFinding {
                Code = ToolErrorCodes.DocsInconsistent,
                Severity = DocsFinding.Warning,
                Message = $"'{fileName}' is not listed in any knowledge/ADR relatedFiles.",
                TargetFile = fileName,
                SuggestedTool = "manage_project_docs",
                FixHint = "Call manage_project_docs action=write kind=memory (or kind=adr) and include this file in relatedFiles."
            });
        }
    }
}
