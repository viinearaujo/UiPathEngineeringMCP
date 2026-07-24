using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Filesystem;

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
        if (projectJsonPath == null)
        {
            throw new FileNotFoundException("project.json not found in the specified directory.", projectPath);
        }

        var model = _parser.Parse(projectJsonPath, projectPath);
        
        // Read README if it exists
        var readmePath = Path.Combine(projectPath, "README.md");
        if (File.Exists(readmePath))
        {
            var readmeContent = _filesystem.ReadAllText(readmePath);
            model.ReadmeSummary = readmeContent.Length > 500 ? readmeContent[..500] + "..." : readmeContent;
        }

        return Task.FromResult(model);
    }
}