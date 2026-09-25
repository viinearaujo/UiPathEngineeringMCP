using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.GapAnalysis;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectModelBuilder : IProjectModelBuilder {
    private const int ReadmeSummaryMaxLength = 500;

    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectJsonParser _parser;
    private readonly XamlWorkflowParser _xamlParser = new();
    private readonly CodedSourceFileParser _codedParser = new();
    private readonly ICSharpContextBuilder? _csharpContextBuilder;

    public ProjectModelBuilder(IFilesystemProvider filesystem, ICSharpContextBuilder? csharpContextBuilder = null) {
        _filesystem = filesystem;
        _parser = new ProjectJsonParser(filesystem);
        _csharpContextBuilder = csharpContextBuilder;
    }

    public async Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        var projectJsonPath = _filesystem.FindProjectJson(projectPath);
        if (projectJsonPath is null) {
            throw new FileNotFoundException("project.json not found in the specified directory.", projectPath);
        }

        var model = _parser.Parse(projectJsonPath, projectPath);
        TryReadReadme(model, projectPath);
        ParseWorkflows(model, projectPath, cancellationToken);
        var codedSources = ParseCodedFiles(model, projectPath, cancellationToken);
        await AttachCodedInvokeEdgesAsync(model, codedSources, projectPath, cancellationToken);
        model.FolderStructure = _filesystem.GetDirectoryTree(projectPath);
        AppendDependencyGraphRisks(model);
        AppendCodedBoundaryRisks(model);
        return model;
    }

    private void TryReadReadme(UiPathProjectModel model, string projectPath) {
        try {
            var readme = _filesystem.ReadAllText(projectPath.TrimEnd('/', '\\') + "/README.md");
            var summary = readme.Trim();
            model.ReadmeSummary = summary.Length > ReadmeSummaryMaxLength
                ? summary[..ReadmeSummaryMaxLength]
                : summary;
        } catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) {
            // README.md is optional; leave ReadmeSummary null.
        }
    }

    private void ParseWorkflows(UiPathProjectModel model, string projectPath, CancellationToken cancellationToken) {
        foreach (var xamlPath in _filesystem.FindXamlFiles(projectPath)) {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(xamlPath) ?? xamlPath;
            var relativePath = WorkflowPath.ToRelativePath(projectPath, xamlPath);
            WorkflowModel workflow;
            try {
                workflow = _xamlParser.Parse(fileName, xamlPath, _filesystem.ReadAllText(xamlPath));
            } catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) {
                workflow = new WorkflowModel {
                    FileName = fileName,
                    FilePath = xamlPath,
                    HasParseError = true,
                    ParseError = $"XAML parse failure: could not read file ({ex.Message})"
                };
            }

            workflow.RelativePath = relativePath;
            workflow.IsMain = WorkflowPath.IsMain(workflow, model.MainWorkflow);
            model.Workflows.Add(workflow);
            model.Variables.AddRange(workflow.Variables);
            model.Arguments.AddRange(workflow.Arguments);
            model.InvokeWorkflows.AddRange(workflow.InvokeWorkflows);
            model.ExceptionHandlers.AddRange(workflow.ExceptionHandlers);

            if (workflow.HasParseError && workflow.ParseError is not null) {
                model.Risks.Add($"{fileName}: {workflow.ParseError}");
            }
        }
    }

    private sealed record CodedSource(CodedWorkflowModel Model, string RelativePath, string? Content);

    private List<CodedSource> ParseCodedFiles(UiPathProjectModel model, string projectPath, CancellationToken cancellationToken) {
        var sources = new List<CodedSource>();
        foreach (var csPath in _filesystem.FindCSharpFiles(projectPath)) {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(csPath) ?? csPath;
            var relativePath = WorkflowPath.ToRelativePath(projectPath, csPath);
            string? content = null;
            CodedWorkflowModel coded;
            try {
                content = _filesystem.ReadAllText(csPath);
                coded = _codedParser.Parse(fileName, csPath, content);
            } catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) {
                coded = new CodedWorkflowModel {
                    FileName = fileName,
                    FilePath = csPath,
                    HasParseError = true,
                    ParseError = $"C# parse failure: could not read file ({ex.Message})"
                };
            }

            model.CodedWorkflows.Add(coded);
            sources.Add(new CodedSource(coded, relativePath, content));

            if (coded.HasParseError && coded.ParseError is not null) {
                model.Risks.Add($"{fileName}: {coded.ParseError}");
            }

            // Graph nodes: coded workflows only (not source helpers or test cases).
            if (string.Equals(coded.Kind, CodedFileKind.Workflow, StringComparison.OrdinalIgnoreCase)) {
                var workflow = new WorkflowModel {
                    FileName = fileName,
                    FilePath = csPath,
                    RelativePath = relativePath,
                    HasParseError = coded.HasParseError,
                    ParseError = coded.ParseError
                };
                workflow.IsMain = WorkflowPath.IsMain(workflow, model.MainWorkflow);
                model.Workflows.Add(workflow);
            }
        }

        return sources;
    }

    private async Task AttachCodedInvokeEdgesAsync(
        UiPathProjectModel model,
        List<CodedSource> codedSources,
        string projectPath,
        CancellationToken cancellationToken) {
        var codedNodes = model.Workflows
            .Where(w => w.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (codedNodes.Count == 0) {
            return;
        }

        CSharpAnalysisContext? analysis = null;
        if (_csharpContextBuilder is not null) {
            try {
                analysis = await _csharpContextBuilder.BuildAsync(projectPath, cancellationToken);
            } catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException) {
                model.Risks.Add($"Coded invoke analysis fell back to syntax-only: {ex.Message}");
            }
        }

        if (analysis is not null) {
            CodedWorkflowInvokeScanner.AttachInvokes(codedNodes, analysis);
        } else {
            // Syntax-only fallback when no compilation is available.
            var byFileName = codedNodes.ToDictionary(w => w.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (var source in codedSources) {
                if (!string.Equals(source.Model.Kind, CodedFileKind.Workflow, StringComparison.OrdinalIgnoreCase)
                    || source.Content is null
                    || !byFileName.TryGetValue(source.Model.FileName, out var node)) {
                    continue;
                }

                var identity = WorkflowPath.Identity(node);
                foreach (var invoke in CodedWorkflowInvokeScanner.Scan(
                    identity, source.Content, semanticModel: null, CSharpAnalysisMode.SyntaxOnly)) {
                    node.InvokeWorkflows.Add(invoke);
                }
            }
        }

        foreach (var node in codedNodes) {
            model.InvokeWorkflows.AddRange(node.InvokeWorkflows);
        }
    }

    private static void AppendDependencyGraphRisks(UiPathProjectModel model) {
        var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow, model.CodedWorkflows);

        foreach (var warning in graph.Warnings) {
            model.Risks.Add(warning);
        }

        foreach (var cycle in graph.Cycles) {
            model.Risks.Add($"Circular workflow dependency detected: {string.Join(" -> ", cycle)}");
        }

        foreach (var orphan in graph.Orphans) {
            model.Risks.Add($"Orphan workflow (not invoked from Main): {orphan}");
        }

        foreach (var edge in graph.Edges.Where(e => !e.IsResolved)) {
            model.Risks.Add($"Unresolved workflow invocation: {edge.Source} -> {edge.Target} (target file not found in project)");
        }
    }

    private static void AppendCodedBoundaryRisks(UiPathProjectModel model) {
        foreach (var gap in XamlCodedInvokeBoundary.Lint(model)) {
            model.Risks.Add(gap.Message);
        }
    }
}
