using UiPath.Engineering.Mcp.Core.Abstractions;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class UpdatePlanTaskDoneGateTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-done-gate-" + Guid.NewGuid().ToString("N"));
    private readonly PlanTestFilesystem _fs;
    private readonly ImplementationPlanStore _store = new();

    public UpdatePlanTaskDoneGateTests() {
        Directory.CreateDirectory(_projectPath);
        _fs = new PlanTestFilesystem { ProjectJson = Path.Combine(_projectPath, "project.json") };
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    [Fact]
    public async Task Done_SucceedsWithoutGeneratedDocs() {
        _store.Save(_projectPath, new ImplementationPlan {
            Goal = "g",
            Tasks = [new PlanTask { Id = "task-1", Title = "Create Main workflow" }]
        });
        var tool = new UpdatePlanTaskTool(_fs, _store);

        var result = await tool.UpdatePlanTask(_projectPath, "task-1", PlanTask.Done);

        Assert.Equal("success", result.Status);
        Assert.Equal(PlanTask.Done, _store.Load(_projectPath)!.Tasks[0].Status);
        Assert.DoesNotContain(result.ErrorDetails, e => e.ErrorCode == UiPath.Engineering.Mcp.Core.ToolErrorCodes.DocsStale);
    }

    private sealed class PlanTestFilesystem : IFilesystemProvider {
        public bool Allowed { get; set; } = true;
        public string? ProjectJson { get; set; }

        public bool IsPathAllowed(string requestedPath) => Allowed;
        public string? FindProjectJson(string projectPath) => ProjectJson;
        public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
        public IReadOnlyList<string> FindCSharpFiles(string projectPath) => [];
        public string ReadAllText(string filePath) => "";
        public long GetFileSize(string filePath) => 0;
        public DateTime GetLastWriteTimeUtc(string filePath) => DateTime.UnixEpoch;
        public DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) => new() { Name = root };
        public void CreateDirectory(string path) { }
        public void WriteAllText(string filePath, string content) { }
        public void DeleteFile(string filePath) { }
        public bool FileExists(string path) => false;
    }
}

public class ImplementUiPathGoalPromptRecipeTests {
    [Fact]
    public void Render_IsThinRecipeOfCopilotLoop() {
        var text = ImplementUiPathGoalPrompt.Render(
            @"C:/Users/arauj/Documents/uipath/perf",
            "Finish dispatcher retries");

        Assert.Contains("copilot-studio-agent-instructions.txt", text);
        Assert.Contains("recommend_activities", text);
        Assert.Contains("validate_project", text);
        Assert.Contains("build:false", text);
        Assert.Contains("update_plan_task", text);
        Assert.Contains("not blocked on docs/ADR freshness", text);
        Assert.DoesNotContain("done requires current docs", text);
        Assert.DoesNotContain("manage_project_docs", text);
    }
}
