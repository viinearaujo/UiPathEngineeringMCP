using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.Git;
using UiPath.Engineering.Mcp.Providers.GitLab;
using UiPath.Engineering.Mcp.Providers.Skills;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

internal sealed class FakeFilesystemProvider : IFilesystemProvider {
    public bool Allowed { get; set; } = true;
    public string? ProjectJson { get; set; } = "/projects/testProcess/project.json";
    public string ProjectJsonContent { get; set; } = string.Empty;
    public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CSharpFiles { get; } = [];
    public Dictionary<string, string> FileContents { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> CreatedDirectories { get; } = [];

    public bool IsPathAllowed(string requestedPath) => Allowed;
    public string? FindProjectJson(string projectPath) => ProjectJson;
    public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
    public IReadOnlyList<string> FindCSharpFiles(string projectPath) => CSharpFiles;
    public string ReadAllText(string filePath) =>
        FileContents.TryGetValue(filePath, out var content) ? content : ProjectJsonContent;
    public DateTime GetLastWriteTimeUtc(string filePath) => DateTime.UnixEpoch;
    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) =>
        new() { Name = Path.GetFileName(root) ?? root, Path = root, IsDirectory = true };

    public void CreateDirectory(string path) => CreatedDirectories.Add(path);
    public void WriteAllText(string filePath, string content) => Writes[filePath] = content;
    public bool FileExists(string path) => ExistingFiles.Contains(path) || FileContents.ContainsKey(path) || Writes.ContainsKey(path);
}

internal sealed class FakeProjectModelBuilder : IProjectModelBuilder {
    public UiPathProjectModel? Model { get; set; }
    public Exception? ToThrow { get; set; }

    public Task<UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) {
            return Task.FromException<UiPathProjectModel>(ToThrow);
        }

        return Task.FromResult(Model ?? new UiPathProjectModel { ProjectName = "testProcess" });
    }
}

internal sealed class FakeUiPathCliProvider : IUiPathCliProvider {
    public UiPathCliResult Result { get; set; } = new() { Success = true, Summary = "Validation completed." };
    public UiPathCliResult RunResult { get; set; } = new() { Success = true, Summary = "Completed." };
    public Exception? ValidateException { get; set; }
    public (bool Validate, bool Build, bool Pack)? LastValidateFlags { get; private set; }
    public string? LastVerb { get; private set; }
    public string? LastArguments { get; private set; }

    public Task<UiPathCliResult> ValidateAsync(
        string projectPath, bool validate, bool build, bool pack, CancellationToken cancellationToken = default) {
        if (ValidateException is not null) {
            throw ValidateException;
        }
        LastValidateFlags = (validate, build, pack);
        return Task.FromResult(Result);
    }

    public Task<UiPathCliResult> RunAsync(
        string verb, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default) {
        LastVerb = verb;
        LastArguments = arguments;
        return Task.FromResult(RunResult);
    }
}

internal sealed class FakeGitProvider : IGitProvider {
    public GitStatusResult StatusResult { get; set; } = new() { IsRepository = true, Branch = "main" };
    public GitLogResult LogResult { get; set; } = new() { IsRepository = true };

    public Task<GitStatusResult> GetStatusAsync(string repoPath, CancellationToken cancellationToken = default)
        => Task.FromResult(StatusResult);

    public Task<GitLogResult> GetRecentCommitsAsync(string repoPath, int count, CancellationToken cancellationToken = default)
        => Task.FromResult(LogResult);
}

internal sealed class FakeGitLabProvider : IGitLabProvider {
    public GitLabIssueListResult SearchResult { get; set; } = new() { Success = true };
    public Func<string, string, IReadOnlyList<string>, GitLabIssueResult>? CreateHandler { get; set; }
    public List<(string Title, string Description)> CreatedIssues { get; } = [];

    public Task<GitLabIssueListResult> SearchIssuesAsync(string query, int maxResults, CancellationToken cancellationToken = default)
        => Task.FromResult(SearchResult);

    public Task<GitLabIssueResult> CreateIssueAsync(string title, string description, IReadOnlyList<string> labels, CancellationToken cancellationToken = default) {
        CreatedIssues.Add((title, description));
        var result = CreateHandler?.Invoke(title, description, labels)
            ?? new GitLabIssueResult {
                Success = true,
                Issue = new GitLabIssueSummary { Iid = CreatedIssues.Count, Title = title, WebUrl = $"https://gitlab.example.com/p/-/issues/{CreatedIssues.Count}" }
            };
        return Task.FromResult(result);
    }
}

internal sealed class FakeSkillsProvider : ISkillsProvider {
    public IReadOnlyList<SkillSummary> Skills { get; set; } = [];
    public SkillReadResult ReadResult { get; set; } = new() {
        Success = true, SkillName = "uipath-rpa", File = "SKILL.md", Content = "# playbook"
    };
    public bool ThrowRootMissing { get; set; }
    public string? LastName { get; private set; }
    public string? LastFile { get; private set; }

    public Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default) {
        if (ThrowRootMissing) {
            throw new DirectoryNotFoundException("Skills root '/missing' does not exist.");
        }
        return Task.FromResult(Skills);
    }

    public Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken cancellationToken = default) {
        LastName = name;
        LastFile = file;
        return Task.FromResult(ReadResult);
    }
}

internal sealed class FakeCSharpAnalysisService : ICSharpAnalysisService {
    public FindSymbolResult SymbolResult { get; set; } = new();
    public FindReferencesResult ReferencesResult { get; set; } = new();
    public CodeContextResult ContextResult { get; set; } = new() { Found = true };
    public CompileDiagnosticsResult DiagnosticsResult { get; set; } = new();
    public Exception? ToThrow { get; set; }
    public string? LastProjectPath { get; private set; }
    public string? LastSymbol { get; private set; }
    public string? LastKind { get; private set; }
    public string? LastFile { get; private set; }
    public int? LastLine { get; private set; }
    public string? LastSeverity { get; private set; }

    public Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol; LastKind = kind;
        return Task.FromResult(SymbolResult);
    }

    public Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol;
        return Task.FromResult(ReferencesResult);
    }

    public Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSymbol = symbol; LastFile = file; LastLine = line;
        return Task.FromResult(ContextResult);
    }

    public Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) throw ToThrow;
        LastProjectPath = projectPath; LastSeverity = severity;
        return Task.FromResult(DiagnosticsResult);
    }
}
