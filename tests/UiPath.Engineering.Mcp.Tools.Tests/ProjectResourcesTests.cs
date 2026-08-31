using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Planning;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ProjectResourcesTests : IDisposable {
    private readonly string _projectPath = Path.Combine(Path.GetTempPath(), "mcp-res-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs = new();
    private readonly FakeProjectModelBuilder _models = new();
    private readonly FakeSkillsProvider _skills = new();
    private readonly ImplementationPlanStore _plans = new();

    public ProjectResourcesTests() {
        Directory.CreateDirectory(_projectPath);
        _fs.ProjectJson = Path.Combine(_projectPath, "project.json");
        File.WriteAllText(_fs.ProjectJson, "{}");
        _fs.Allowed = true;
    }

    public void Dispose() {
        if (Directory.Exists(_projectPath)) {
            Directory.Delete(_projectPath, recursive: true);
        }
    }

    private ProjectResources Create() =>
        new(_fs, new PathPolicy([_projectPath]), _models, _plans, _skills, DocsSupport.Knowledge(_fs), DocsSupport.Adrs(_fs), DocsSupport.Validator(_fs));

    [Fact]
    public async Task Skill_ReturnsRedactedPlaybook() {
        _skills.ReadResult = new SkillReadResult {
            Success = true, SkillName = "uipath-rpa", File = "SKILL.md",
            Content = "token=supersecret"
        };

        var text = await Create().GetSkill("uipath-rpa");

        Assert.Contains("***REDACTED***", text);
        Assert.DoesNotContain("supersecret", text);
    }

    [Fact]
    public async Task Model_ReturnsSummaryJsonWithoutActivityTrees() {
        _models.Model = new UiPathProjectModel {
            ProjectName = "perf",
            Workflows = [new WorkflowModel {
                FileName = "Main.xaml",
                Activities = [new ActivityModel { Id = "sequence.1", Type = "Sequence", DisplayName = "Main" }]
            }]
        };

        var json = await Create().GetProjectModel(_projectPath);

        Assert.Contains("\"projectName\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sequence.1", json);
    }

    [Fact]
    public void Plan_ReadsFixedJsonPathOnly() {
        _plans.Save(_projectPath, new ImplementationPlan { Goal = "g", Tasks = [] });
        var diskPath = ImplementationPlanStore.GetJsonPath(_projectPath);
        Assert.True(PathPolicy.TryResolveProjectRelative(_projectPath, "docs/implementation-plan.json", out var path));
        _fs.FileContents[path] = File.ReadAllText(diskPath);

        var json = Create().GetProjectPlan(_projectPath);
        Assert.Contains("\"Goal\": \"g\"", json);
        Assert.True(File.Exists(Path.Combine(_projectPath, "docs", "implementation-plan.json")));
    }

    [Fact]
    public void Plan_WhenMissing_ExplainsFixedPath() {
        var text = Create().GetProjectPlan(_projectPath);
        Assert.Contains("docs/implementation-plan.json", text);
    }

    [Fact]
    public void Workflow_RefusesEnvFile() {
        var text = Create().GetWorkflow(_projectPath, ".env");
        Assert.Contains("cannot be read", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_RefusesUppercasePemExtension() {
        var text = Create().GetWorkflow(_projectPath, "certs/server.PEM");
        Assert.Contains("cannot be read", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Workflow_OversizedFile_RejectedBeforeRead() {
        const string relative = "Main.xaml";
        Assert.True(PathPolicy.TryResolveProjectRelative(_projectPath, relative, out var target));
        _fs.FileContents[target] = "tiny";
        _fs.FileSizes[target] = FileReadLimits.MaxFileBytes + 1L;

        var text = Create().GetWorkflow(_projectPath, relative);

        Assert.Contains("too large", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_OversizedFile_RejectedBeforeRead() {
        Assert.True(PathPolicy.TryResolveProjectRelative(_projectPath, "docs/implementation-plan.json", out var path));
        _fs.FileContents[path] = "tiny";
        _fs.FileSizes[path] = FileReadLimits.MaxFileBytes + 1L;

        var text = Create().GetProjectPlan(_projectPath);

        Assert.Contains("too large", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Knowledge_ReturnsCombinedIndexAndContextFlags() {
        var json = await Create().GetProjectKnowledge(_projectPath);

        Assert.Contains("\"memory\":", json);
        Assert.Contains("\"adrs\":", json);
        Assert.Contains("\"stale\":", json);
        Assert.Contains("\"missing\":", json);
    }

    [Fact]
    public async Task Model_WhenBuilderThrows_ReturnsErrorString() {
        _models.ToThrow = new InvalidOperationException("builder exploded");

        var text = await Create().GetProjectModel(_projectPath);

        Assert.Contains("failed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("builder exploded", text);
    }
}
