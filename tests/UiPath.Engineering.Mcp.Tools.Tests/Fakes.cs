using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

internal sealed class FakeFilesystemProvider : IFilesystemProvider
{
    public bool Allowed { get; set; } = true;
    public string? ProjectJson { get; set; } = "/projects/testProcess/project.json";

    public bool IsPathAllowed(string requestedPath) => Allowed;
    public string? FindProjectJson(string projectPath) => ProjectJson;
    public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
    public string ReadAllText(string filePath) => string.Empty;
}

internal sealed class FakeProjectModelBuilder : IProjectModelBuilder
{
    public UiPathProjectModel? Model { get; set; }
    public Exception? ToThrow { get; set; }

    public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        if (ToThrow is not null)
        {
            return Task.FromException<UiPathProjectModel>(ToThrow);
        }

        return Task.FromResult(Model ?? new UiPathProjectModel { ProjectName = "testProcess" });
    }
}

internal sealed class FakeUiPathCliProvider : IUiPathCliProvider
{
    public UiPathCliResult Result { get; set; } = new() { Success = true, Summary = "Validation completed." };

    public Task<UiPathCliResult> ValidateAsync(
        string projectPath, bool restore, bool analyze, bool pack, CancellationToken cancellationToken = default)
        => Task.FromResult(Result);
}
