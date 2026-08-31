using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class WriteWorkflowFileTool {
    private static readonly string[] AllowedExtensions = [".xaml", ".cs"];

    private readonly IFilesystemProvider _filesystem;
    private readonly IActivityCatalogResolver _catalogResolver;

    public WriteWorkflowFileTool(IFilesystemProvider filesystem, IActivityCatalogResolver catalogResolver) {
        _filesystem = filesystem;
        _catalogResolver = catalogResolver;
    }

    [McpServerTool(UseStructuredContent = true), Description("Leave-off full-file overwrite (ToolSurface=All only). Prefer edit_workflow_file for small string edits and insert_activities for spec-based XAML inserts. Creates or fully overwrites a .xaml or .cs file. For .xaml, activity types must be in the project catalog unless allowUnknownActivities is true.")]
    public async Task<ToolResult> WriteWorkflowFile(
        [Description("Absolute path to the UiPath project directory (must contain project.json).")] string projectPath,
        [Description("Path of the file relative to the project root, e.g. 'Main.xaml' or 'Workflows/SendEmail.xaml'.")] string relativePath,
        [Description("Full new content of the file.")] string content,
        [Description("Escape hatch: when true, skip the catalog check for .xaml. Default false — refuse XAML that contains activity types not in the project's catalog.")] bool allowUnknownActivities = false,
        CancellationToken cancellationToken = default) {

        var sw = Stopwatch.StartNew();

        if (ToolResults.GuardProject(_filesystem, projectPath, sw) is { } guardFailure) {
            return guardFailure;
        }

        if (string.IsNullOrWhiteSpace(relativePath)) {
            return ToolResults.Failure("relativePath is required.", sw);
        }

        var extension = Path.GetExtension(relativePath);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) {
            return ToolResults.Failure($"Only {string.Join(" and ", AllowedExtensions)} files can be written; got '{extension}'.", sw);
        }

        if (!ToolResults.TryResolveWithinProject(projectPath, relativePath, out var targetPath)) {
            return ToolResults.Failure("relativePath must resolve to a location inside the project directory.", sw);
        }

        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) && !allowUnknownActivities) {
            var catalog = await _catalogResolver.ResolveAsync(projectPath, cancellationToken);
            var unknown = XamlCatalogGuard.FindUnknownActivities(content ?? string.Empty, catalog);
            if (unknown.Count > 0) {
                return ToolResults.Failure(
                    $"Refusing to write '{relativePath}': {unknown.Count} activity type(s) are not in the project catalog.",
                    unknown, sw);
            }
        }

        content ??= string.Empty;

        var existed = _filesystem.FileExists(targetPath);

        var directory = Path.GetDirectoryName(targetPath)!;
        _filesystem.CreateDirectory(directory);
        _filesystem.WriteAllText(targetPath, content);

        var utf8 = Encoding.UTF8.GetBytes(content);
        var sha256 = Convert.ToHexString(SHA256.HashData(utf8));
        string? className = null;
        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase)) {
            const string marker = "x:Class=\"";
            var start = content.IndexOf(marker, StringComparison.Ordinal);
            if (start >= 0) {
                start += marker.Length;
                var end = content.IndexOf('"', start);
                if (end > start) {
                    className = content[start..end];
                }
            }
        } else if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) {
            var match = System.Text.RegularExpressions.Regex.Match(content, @"\bclass\s+(\w+)");
            if (match.Success) {
                className = match.Groups[1].Value;
            }
        }

        return ToolResults.Ok(
            existed ? $"Updated '{relativePath}'." : $"Created '{relativePath}'.",
            new {
                filePath = targetPath,
                bytesWritten = content.Length,
                sha256,
                className,
                overwritten = existed
            }, sw);
    }
}
