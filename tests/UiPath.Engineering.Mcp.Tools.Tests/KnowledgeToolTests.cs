using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Core.Knowledge;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Providers.Skills;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class KnowledgeToolTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcp-kn-tools-" + Guid.NewGuid().ToString("N"));
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mcp-kn-proj-" + Guid.NewGuid().ToString("N"));
    private readonly FakeFilesystemProvider _fs = new();

    public KnowledgeToolTests() {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_project);
        _fs.ProjectJson = Path.Combine(_project, "project.json");
        _fs.Allowed = true;
    }

    public void Dispose() {
        foreach (var dir in new[] { _root, _project }) {
            if (Directory.Exists(dir)) {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private void WriteVendored(string relative, string content) {
        var path = Path.Combine(_root, "skills", "uipath-rpa", "references", relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private SkillsOptions SkillsConfig() => new() { SkillsRoot = Path.Combine(_root, "skills") };

    private SearchKnowledgeTool Tool(ISkillsProvider? skills = null) =>
        new(_fs, Microsoft.Extensions.Options.Options.Create(SkillsConfig()), skills ?? new FakeSkillsProvider());

    private static JsonElement Data(ToolResult result) => JsonSerializer.SerializeToElement(result.Data);

    [Fact]
    public async Task SearchKnowledge_ActivityDocs_ReadsTheVendoredSnapshotWhenTheProjectHasNoLocalDocs() {
        WriteVendored("activity-docs/UiPath.System.Activities/26.4/activities/RetryScope.md",
            "# Retry Scope\n\n## Properties\n\nRetryInterval controls the wait between attempts.\n");

        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeActivityDocs, "RetryInterval", _project);
        var excerpts = Data(result).GetProperty("excerpts").EnumerateArray().ToList();
        var excerpt = Assert.Single(excerpts);
        Assert.Equal("RetryScope", excerpt.GetProperty("activity").GetString());
        Assert.Contains("RetryInterval", excerpt.GetProperty("snippet").GetString());
        Assert.True(excerpt.GetProperty("line").GetInt32() > 0);
        Assert.Contains(result.Warnings, w => w.Contains("No project-local package docs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchKnowledge_ActivityDocs_PrefersProjectLocalDocsAndLabelsTheSource() {
        WriteVendored("activity-docs/UiPath.System.Activities/26.4/activities/RetryScope.md",
            "# Retry Scope\n\nVendored description of the wait.\n");

        var localDir = Path.Combine(_project, ".local", "docs", "packages", "UiPath.System.Activities", "26.4", "activities");
        Directory.CreateDirectory(localDir);
        File.WriteAllText(Path.Combine(localDir, "RetryScope.md"),
            "# Retry Scope\n\nProject-local description of the wait between attempts.\n");

        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeActivityDocs, "wait between attempts", _project);
        var excerpts = Data(result).GetProperty("excerpts").EnumerateArray().ToList();

        Assert.Contains(excerpts, e => e.GetProperty("source").GetString() == "project-local");
        Assert.Contains(excerpts, e => e.GetProperty("source").GetString() == "vendored");
    }

    [Fact]
    public async Task SearchKnowledge_ActivityDocs_WhenNoCorpusExists_ReturnsAnActionableError() {
        var tool = new SearchKnowledgeTool(
            _fs,
            Microsoft.Extensions.Options.Options.Create(new SkillsOptions { SkillsRoot = Path.Combine(_root, "absent") }),
            new FakeSkillsProvider());

        var result = await tool.SearchKnowledge(SearchKnowledgeTool.ModeActivityDocs, "anything", _project);

        Assert.Equal("error", result.Status);
        Assert.Equal(Core.ToolErrorCodes.OperationFailed, result.ErrorDetails[0].ErrorCode);
    }

    [Fact]
    public async Task SearchKnowledge_ActivityDocs_NoMatchWarnsRatherThanFails() {
        WriteVendored("activity-docs/UiPath.System.Activities/26.4/activities/LogMessage.md", "# Log Message\n\nLogs a message.\n");

        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeActivityDocs, "zzz-not-present", _project);

        Assert.Equal("success", result.Status);
        Assert.Empty(Data(result).GetProperty("excerpts").EnumerateArray());
        Assert.Contains(result.Warnings, w => w.Contains("No documentation matched", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchKnowledge_All_IncludesActivityDocsThroughTheGuidesRoot() {
        WriteVendored("xaml/xaml-basics-and-rules.md", "# XAML Basics\n\nRule 24 requires a Sequence wrap in every slot.\n");
        WriteVendored("activity-docs/UiPath.System.Activities/26.4/activities/LogMessage.md", "# Log Message\n\nRule 24 also applies here.\n");

        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeKnowledge, "Rule 24", kind: SearchKnowledgeTool.KnowledgeAll);

        Assert.Equal("success", result.Status);
        var relativePaths = Data(result).GetProperty("excerpts").EnumerateArray()
            .Select(e => e.GetProperty("relativePath").GetString()).ToList();
        Assert.Contains(relativePaths, p => p!.Contains("xaml-basics-and-rules.md", StringComparison.Ordinal));
        Assert.Contains(relativePaths, p => p!.Contains("LogMessage.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchKnowledge_ActivityDocsKind_DropsTheGuides() {
        WriteVendored("xaml/xaml-basics-and-rules.md", "# XAML Basics\n\nRule 24 requires a Sequence wrap.\n");
        WriteVendored("activity-docs/UiPath.System.Activities/26.4/activities/LogMessage.md", "# Log Message\n\nThe Rule 24 wrap lives here.\n");

        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeKnowledge, "Rule 24", kind: SearchKnowledgeTool.KnowledgeActivityDocs);
        var relativePaths = Data(result).GetProperty("excerpts").EnumerateArray()
            .Select(e => e.GetProperty("relativePath").GetString()).ToList();

        Assert.Contains(relativePaths, p => p!.Contains("LogMessage.md", StringComparison.Ordinal));
        Assert.DoesNotContain(relativePaths, p => p!.Contains("xaml-basics-and-rules.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchKnowledge_AdvertisesItsKindsAndRejectsAnUnknownOne() {
        var result = await Tool().SearchKnowledge(SearchKnowledgeTool.ModeKnowledge, "x", kind: "nonsense");

        Assert.Equal("error", result.Status);
        Assert.Equal(Core.ToolErrorCodes.InvalidArgument, result.ErrorDetails[0].ErrorCode);
        Assert.Equal(["all", "guides", "activityDocs"], SearchKnowledgeTool.KnowledgeKinds);
    }

    [Fact]
    public async Task SearchKnowledge_MissingCorpusIsAnActionableError() {
        var tool = new SearchKnowledgeTool(
            _fs,
            Microsoft.Extensions.Options.Options.Create(new SkillsOptions { SkillsRoot = Path.Combine(_root, "absent") }),
            new FakeSkillsProvider());

        var result = await tool.SearchKnowledge(SearchKnowledgeTool.ModeKnowledge, "anything");

        Assert.Equal("error", result.Status);
        Assert.Equal(Core.ToolErrorCodes.SkillsRootMissing, result.ErrorDetails[0].ErrorCode);
    }
}
