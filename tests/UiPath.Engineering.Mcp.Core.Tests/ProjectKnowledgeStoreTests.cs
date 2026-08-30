using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectKnowledgeStoreTests {
    private readonly FakeFilesystemProvider _fs = new();
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mcp-knowledge-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Upsert_WritesArticleAndIndex() {
        var store = new ProjectKnowledgeStore(_fs);

        var article = store.Upsert(_project, "retry-policy", "Retry policy", "Use exponential backoff.", ["Main.xaml"], KnowledgeArticle.Current);

        Assert.Equal("retry-policy", article.Id);
        Assert.True(_fs.FileExists(ProjectDocsPaths.KnowledgeArticle(_project, "retry-policy")));
        Assert.Contains("exponential backoff", _fs.ReadAllText(ProjectDocsPaths.KnowledgeArticle(_project, "retry-policy")));
        Assert.Single(store.Load(_project).Articles);
    }

    [Fact]
    public void Upsert_RejectsInvalidId() {
        var store = new ProjectKnowledgeStore(_fs);
        Assert.Throws<ArgumentException>(() => store.Upsert(_project, "Not Valid", "t", "body", null, null));
    }

    [Fact]
    public void Delete_RemovesArticleAndIndexRow() {
        var store = new ProjectKnowledgeStore(_fs);
        store.Upsert(_project, "retry-policy", "Retry policy", "body", null, null);

        Assert.True(store.Delete(_project, "retry-policy"));
        Assert.False(_fs.FileExists(ProjectDocsPaths.KnowledgeArticle(_project, "retry-policy")));
        Assert.Empty(store.Load(_project).Articles);
    }
}
