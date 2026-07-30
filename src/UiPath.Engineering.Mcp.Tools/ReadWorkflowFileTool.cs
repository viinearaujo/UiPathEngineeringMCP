using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class ReadWorkflowFileTool {
    private const int DefaultMaxLines = 1000;
    private static readonly string[] BlockedExtensions = [".pem", ".key"];

    private readonly IFilesystemProvider _filesystem;

    public ReadWorkflowFileTool(IFilesystemProvider filesystem) {
        _filesystem = filesystem;
    }

    [McpServerTool, Description("Reads the contents of any text file inside a UiPath project (XAML, .cs, JSON, configs, docs), with line numbers and pagination. Use this whenever the user asks what a file contains, to show specific lines, or to inspect project configuration. Obvious secret values are redacted; .env, *.pem and *.key files are refused. Use startLine/lineCount to page through large files.")]
    public ToolResult ReadWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.cs' or 'Data/Config.json'.")] string relativePath,
        [Description("1-based first line to return; omit to start at line 1.")] int? startLine = null,
        [Description("Maximum number of lines to return (default 1000).")] int? lineCount = null) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        var fileName = Path.GetFileName(relativePath);
        var extension = Path.GetExtension(relativePath);
        if (fileName.StartsWith(".env", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || BlockedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return ToolResults.Failure($"'{relativePath}' looks like a secret or key file and cannot be read.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (!_filesystem.FileExists(targetPath)) {
            return ToolResults.Failure($"File '{relativePath}' does not exist in the project.", sw);
        }

        string raw;
        try {
            raw = _filesystem.ReadAllText(targetPath);
        } catch (Exception) {
            return ToolResults.Failure($"'{relativePath}' could not be read as text (it may be binary).", sw);
        }

        if (raw.Contains('\0')) {
            return ToolResults.Failure($"'{relativePath}' appears to be a binary file; only text files can be read.", sw);
        }

        var (redacted, redactedCount) = SecretRedactor.Redact(raw);
        var lines = redacted.Replace("\r\n", "\n").Split('\n');

        var start = Math.Max(1, startLine ?? 1);
        if (start > lines.Length) {
            return ToolResults.Failure($"startLine {start} is past the end of the file ({lines.Length} lines).", sw);
        }

        var count = Math.Min(Math.Max(1, lineCount ?? DefaultMaxLines), lines.Length - start + 1);

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++) {
            sb.Append(start + i).Append('\t').Append(lines[start - 1 + i]).Append('\n');
        }

        var truncated = start - 1 + count < lines.Length;

        return ToolResults.Ok(
            truncated
                ? $"Read lines {start}-{start + count - 1} of {lines.Length} from '{relativePath}' (truncated)."
                : $"Read {count} line(s) from '{relativePath}'.",
            new {
                filePath = targetPath,
                content = sb.ToString(),
                totalLines = lines.Length,
                returnedLines = count,
                truncated,
                redactedCount
            }, sw);
    }
}
