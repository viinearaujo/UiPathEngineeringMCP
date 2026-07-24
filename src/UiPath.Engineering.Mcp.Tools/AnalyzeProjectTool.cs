using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Filesystem;
using System.ComponentModel;

namespace UiPath.Engineering.Mcp.Tools;

[McpServerToolType]
public sealed class AnalyzeProjectTool {
    private readonly IFilesystemProvider _filesystem;

    public AnalyzeProjectTool(IFilesystemProvider filesystem) => _filesystem = filesystem;

    [McpServerTool, Description("Analyzes a UiPath project and returns structured metadata, workflows, and dependencies.")]
    public ToolResult AnalyzeProject([Description("Absolute path to the UiPath project directory.")] string projectPath) {
        var sw = Stopwatch.StartNew();
        
        if (!_filesystem.IsPathAllowed(projectPath)) {
            return new ToolResult {
                Status = "error",
                Summary = "Path not allowed.",
                Errors = ["The requested path is outside the allowed project roots."],
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var projectJsonPath = _filesystem.FindProjectJson(projectPath);
        if (projectJsonPath == null) {
            return new ToolResult {
                Status = "error",
                Summary = "project.json not found.",
                Errors = ["Could not locate project.json in the specified directory."],
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var jsonContent = _filesystem.ReadAllText(projectJsonPath);
        var doc = JsonDocument.Parse(jsonContent);
        var root = doc.RootElement;

        var dependencies = new List<string>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object) {
            dependencies = deps.EnumerateObject().Select(p => $"{p.Name} ({p.Value.GetString()})").ToList();
        }

        var model = new UiPathProjectModel {
            ProjectPath = projectPath,
            ProjectName = root.TryGetProperty("name", out var name) ? name.GetString() ?? "Unknown" : "Unknown",
            MainWorkflow = root.TryGetProperty("main", out var main) ? main.GetString() : null,
            ProjectJsonPath = projectJsonPath,
            Workflows = _filesystem.FindXamlFiles(projectPath).Select(Path.GetFileName).Where(f => f != null).Cast<string>().ToList(),
            Dependencies = dependencies
        };

        return new ToolResult {
            Summary = "Project analyzed successfully.",
            Data = model,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}