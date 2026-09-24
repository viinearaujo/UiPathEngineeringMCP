using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

/// <summary>
/// Hand-written CLI double for the verb tools: records the exact tokens it was asked to run and
/// replays a canned envelope. No mocking framework.
/// </summary>
internal sealed class RecordingStructuredCli : IUiPathCliProvider {
    public string StdOut { get; set; } = """{"Result":"Success","Data":{}}""";
    public bool CliSuccess { get; set; } = true;
    public int ExitCode { get; set; }
    public List<string> CliErrors { get; } = [];
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public List<string> WorkingDirectories { get; } = [];
    public List<int?> Timeouts { get; } = [];
    public Exception? ToThrow { get; set; }

    public IReadOnlyList<string>? LastTokens => Calls.Count > 0 ? Calls[^1] : null;
    public string LastArguments => LastTokens is null ? string.Empty : CliVerbArguments.ToArgumentString(LastTokens);

    public Task<UiPathCliResult> ValidateAsync(string projectPath, bool validate, bool build, bool pack, CancellationToken cancellationToken = default) =>
        Task.FromResult(new UiPathCliResult { Success = true, Summary = "Validation completed." });

    public Task<UiPathCliResult> RunAsync(string verb, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default) {
        if (ToThrow is not null) {
            throw ToThrow;
        }

        Calls.Add(SplitQuoted(arguments));
        WorkingDirectories.Add(workingDirectory ?? string.Empty);
        Timeouts.Add(null);
        return Task.FromResult(new UiPathCliResult {
            Success = CliSuccess, ExitCode = ExitCode, Summary = verb,
            Errors = [.. CliErrors], StdOut = StdOut
        });
    }

    public Task<UiPathCliRunResult> RunStructuredAsync(
        string verb,
        IReadOnlyList<string> tokens,
        string? workingDirectory = null,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default) {
        if (ToThrow is not null) {
            throw ToThrow;
        }

        Calls.Add(tokens);
        WorkingDirectories.Add(workingDirectory ?? string.Empty);
        Timeouts.Add(timeoutSeconds);

        var cli = new UiPathCliResult {
            Success = CliSuccess,
            ExitCode = ExitCode,
            Summary = $"'{verb}' completed.",
            Command = $"uip {CliVerbArguments.ToArgumentString(tokens)}",
            Errors = [.. CliErrors],
            StdOut = StdOut
        };

        return Task.FromResult(new UiPathCliRunResult {
            Cli = cli,
            Envelope = CliEnvelopeParser.TryParse(StdOut, out var envelope) ? envelope : null
        });
    }

    // ProcessRunner is internal to the Providers assembly, so mirror its tokenizer here.
    private static List<string> SplitQuoted(string arguments) {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in arguments) {
            if (c == '"') {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes) {
                if (current.Length > 0) {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}

internal static class CliToolFixtures {
    public const string ProjectPath = @"C:\projects\testProcess";

    public static UiPathCliOptions Options(bool mutating = false, bool execution = false) => new() {
        EnableMutatingCommands = mutating,
        EnableExecution = execution
    };

    public static CliCommandPolicy Policy(bool mutating = false, bool execution = false) =>
        new(Options(mutating, execution));

    public static FakeFilesystemProvider ProjectFilesystem(params string[] relativeFiles) {
        var filesystem = new FakeFilesystemProvider();
        foreach (var relative in relativeFiles) {
            filesystem.ExistingFiles.Add(Path.Combine(ProjectPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        }

        return filesystem;
    }

    /// <summary>
    /// Allows the project itself (so the project guard passes) but refuses every other path, to
    /// exercise the per-argument path guards.
    /// </summary>
    public static SelectiveFilesystem ProjectOnlyFilesystem() => new(ProjectPath);

    /// <summary>Value of the token immediately following <paramref name="flag"/>.</summary>
    public static string? TokenAfter(IReadOnlyList<string>? tokens, string flag) {
        if (tokens is null) {
            return null;
        }

        for (var i = 0; i < tokens.Count - 1; i++) {
            if (string.Equals(tokens[i], flag, StringComparison.Ordinal)) {
                return tokens[i + 1];
            }
        }

        return null;
    }

    public static T? Data<T>(ToolResult result) where T : class {
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json, DataOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions DataOptions = new() {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip
    };
}

/// <summary>
/// Allows the project directory (so the project guard passes) and refuses every other path, to
/// exercise the per-argument path guards without touching the shared fake.
/// </summary>
internal sealed class SelectiveFilesystem : IFilesystemProvider {
    private readonly FakeFilesystemProvider _inner = new();
    private readonly string _projectPath;

    public SelectiveFilesystem(string projectPath) => _projectPath = projectPath;

    /// <summary>Seeds a path the tools can resolve as an existing file.</summary>
    public SelectiveFilesystem WithFile(string fullPath) {
        _inner.ExistingFiles.Add(fullPath);
        return this;
    }

    public bool IsPathAllowed(string requestedPath) =>
        string.Equals(requestedPath, _projectPath, StringComparison.OrdinalIgnoreCase)
        || requestedPath.StartsWith(_projectPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || requestedPath.StartsWith(_projectPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public string? FindProjectJson(string projectPath) => IsPathAllowed(projectPath) ? _inner.FindProjectJson(projectPath) : null;
    public IReadOnlyList<string> FindXamlFiles(string projectPath) => _inner.FindXamlFiles(projectPath);
    public IReadOnlyList<string> FindCSharpFiles(string projectPath) => _inner.FindCSharpFiles(projectPath);
    public string ReadAllText(string filePath) => _inner.ReadAllText(filePath);
    public long GetFileSize(string filePath) => _inner.GetFileSize(filePath);
    public DateTime GetLastWriteTimeUtc(string filePath) => _inner.GetLastWriteTimeUtc(filePath);
    public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) => _inner.GetDirectoryTree(root, maxDepth);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public void WriteAllText(string filePath, string content) => _inner.WriteAllText(filePath, content);
    public void DeleteFile(string filePath) => _inner.DeleteFile(filePath);

    public bool FileExists(string path) => _inner.FileExists(path);
}
