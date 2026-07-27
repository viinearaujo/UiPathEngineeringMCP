using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Abstractions;

namespace UiPath.Engineering.Mcp.Core.Parsing;

public sealed class ProjectModelBuilder : IProjectModelBuilder
{
    private readonly IFilesystemProvider _filesystem;
    private readonly ProjectJsonParser _parser;

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
        return Task.FromResult(model);
    }
}
