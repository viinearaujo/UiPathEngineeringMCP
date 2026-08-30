using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Docs;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectDocsValidatorTests {
    private readonly FakeFilesystemProvider _fs = new();
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mcp-docs-val-" + Guid.NewGuid().ToString("N"));

    private ProjectDocsValidator CreateSut() =>
        new(_fs, new ProjectKnowledgeStore(_fs), new ProjectAdrStore(_fs));

    private UiPathProjectModel Model() => new() {
        ProjectPath = _project,
        ProjectName = "demo",
        Workflows = [new WorkflowModel { FileName = "Main.xaml" }]
    };

    [Fact]
    public void MissingGeneratedContext_IsError() {
        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsStale && f.Severity == DocsFinding.Error);
    }

    [Fact]
    public void StaleMetadata_IsError() {
        var renderer = new ProjectContextRenderer(_fs);
        renderer.Sync(_project, new UiPathProjectModel { ProjectName = "demo", ProjectPath = _project });

        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsStale && f.Message.Contains("stale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RelatedFileNewerThanArticle_IsError() {
        var knowledge = new ProjectKnowledgeStore(_fs);
        knowledge.Upsert(_project, "retry", "Retry", "body", ["Main.xaml"], KnowledgeArticle.Current);
        var related = ProjectFilePolicy.CombineProject(_project, "Main.xaml");
        _fs.FileContents[related] = "<x/>";
        _fs.WriteTimesUtc[related] = DateTime.UtcNow.AddHours(1);
        new ProjectContextRenderer(_fs).Sync(_project, Model());

        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsStale && f.Message.Contains("retry"));
    }

    [Fact]
    public void MissingRelatedFile_IsError() {
        var knowledge = new ProjectKnowledgeStore(_fs);
        knowledge.Upsert(_project, "retry", "Retry", "body", ["Gone.xaml"], KnowledgeArticle.Current);
        new ProjectContextRenderer(_fs).Sync(_project, Model());

        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsInconsistent && f.TargetFile == "Gone.xaml");
    }

    [Fact]
    public void UnindexedMarkdown_IsError() {
        new ProjectContextRenderer(_fs).Sync(_project, Model());
        var orphan = ProjectDocsPaths.KnowledgeArticle(_project, "orphan");
        _fs.WriteAllText(orphan, "# orphan");

        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsInconsistent && f.Message.Contains("Unindexed"));
    }

    [Fact]
    public void IncompleteAdr_IsError() {
        var adrPath = ProjectDocsPaths.AdrFile(_project, "0001-incomplete.md");
        _fs.WriteAllText(ProjectDocsPaths.AdrIndex(_project), """
            { "adrs": [ { "id": "0001-incomplete", "number": 1, "title": "Incomplete", "fileName": "0001-incomplete.md", "status": "accepted", "relatedFiles": [], "updatedUtc": "2020-01-01T00:00:00Z" } ] }
            """);
        _fs.WriteAllText(adrPath, "# Incomplete\n");
        new ProjectContextRenderer(_fs).Sync(_project, Model());

        var findings = CreateSut().Validate(_project, Model());

        Assert.Contains(findings, f => f.Code == ToolErrorCodes.DocsAdrIncomplete);
    }

    [Fact]
    public void ProposedAdr_IsWarning() {
        var store = new ProjectAdrStore(_fs);
        store.Write(_project, "Use queues", ProjectAdrStore.RenderTemplate("Use queues", AdrRecord.Proposed, "c", "d", "e"), null, AdrRecord.Proposed, null);
        new ProjectContextRenderer(_fs).Sync(_project, new UiPathProjectModel { ProjectPath = _project, ProjectName = "demo" });

        var findings = CreateSut().Validate(_project, new UiPathProjectModel { ProjectPath = _project, ProjectName = "demo" });

        Assert.Contains(findings, f => f.Severity == DocsFinding.Warning && f.Message.Contains("proposed"));
    }
}
