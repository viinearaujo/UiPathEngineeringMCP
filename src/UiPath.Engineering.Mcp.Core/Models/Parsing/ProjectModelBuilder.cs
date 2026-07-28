using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectModelBuilder : IProjectModelBuilder
{
    private const int ReadmeSummaryMaxLength = 500;

    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectJsonParser _parser;
    private readonly XamlWorkflowParser _xamlParser = new();

    public ProjectModelBuilder(IFilesystemProvider filesystem)
    {
        _filesystem = filesystem;
        _parser = new ProjectJsonParser(filesystem);
    }

    public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var projectJsonPath = _filesystem.FindProjectJson(projectPath);
        if (projectJsonPath is null)
        {
            throw new FileNotFoundException("project.json not found in the specified directory.", projectPath);
        }

        var model = _parser.Parse(projectJsonPath, projectPath);
        TryReadReadme(model, projectPath);
        ParseWorkflows(model, projectPath, cancellationToken);
        model.FolderStructure = _filesystem.GetDirectoryTree(projectPath);
        AppendDependencyGraphRisks(model);
        return Task.FromResult(model);
    }

    private void TryReadReadme(UiPathProjectModel model, string projectPath)
    {
        try
        {
            var readme = _filesystem.ReadAllText(projectPath.TrimEnd('/', '\\') + "/README.md");
            var summary = readme.Trim();
            model.ReadmeSummary = summary.Length > ReadmeSummaryMaxLength
                ? summary[..ReadmeSummaryMaxLength]
                : summary;
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            // README.md is optional; leave ReadmeSummary null.
        }
    }

    private void ParseWorkflows(UiPathProjectModel model, string projectPath, CancellationToken cancellationToken)
    {
        foreach (var xamlPath in _filesystem.FindXamlFiles(projectPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(xamlPath) ?? xamlPath;
            WorkflowModel workflow;
            try
            {
                workflow = _xamlParser.Parse(fileName, xamlPath, _filesystem.ReadAllText(xamlPath));
            }
            catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                workflow = new WorkflowModel
                {
                    FileName = fileName,
                    FilePath = xamlPath,
                    HasParseError = true,
                    ParseError = $"XAML parse failure: could not read file ({ex.Message})"
                };
            }

            workflow.IsMain = string.Equals(fileName, model.MainWorkflow, StringComparison.OrdinalIgnoreCase);
            model.Workflows.Add(workflow);
            model.Variables.AddRange(workflow.Variables);
            model.Arguments.AddRange(workflow.Arguments);
            model.InvokeWorkflows.AddRange(workflow.InvokeWorkflows);
            model.ExceptionHandlers.AddRange(workflow.ExceptionHandlers);

            if (workflow.HasParseError && workflow.ParseError is not null)
            {
                model.Risks.Add($"{fileName}: {workflow.ParseError}");
            }
        }
    }

    private static void AppendDependencyGraphRisks(UiPathProjectModel model)
    {
        var graph = DependencyGraphBuilder.Build(model.Workflows, model.MainWorkflow);

        foreach (var cycle in graph.Cycles)
        {
            model.Risks.Add($"Circular workflow dependency detected: {string.Join(" -> ", cycle)}");
        }

        foreach (var orphan in graph.Orphans)
        {
            model.Risks.Add($"Orphan workflow (not invoked from Main): {orphan}");
        }

        foreach (var edge in graph.Edges.Where(e => !e.IsResolved))
        {
            model.Risks.Add($"Unresolved workflow invocation: {edge.Source} -> {edge.Target} (target file not found in project)");
        }
    }
}
