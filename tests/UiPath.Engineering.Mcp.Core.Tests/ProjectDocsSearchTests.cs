using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectDocsSearchTests {
    [Fact]
    public void Search_KindFilter_LimitsFiles() {
        var fs = new FakeFilesystemProvider();
        var project = Path.Combine(Path.GetTempPath(), "mcp-search-" + Guid.NewGuid().ToString("N"));
        var knowledge = new ProjectKnowledgeStore(fs);
        var adrs = new ProjectAdrStore(fs);
        knowledge.Upsert(project, "retry-policy", "Retry", "Use queues for retry.", ["Main.xaml"], null);
        adrs.Write(project, "Use queues", ProjectAdrStore.RenderTemplate("Use queues", AdrRecord.Accepted, "Need retry.", "Use queues.", "Ops cost."), null, AdrRecord.Accepted, null);
        var search = new ProjectDocsSearch(fs, knowledge, adrs);

        var memory = search.Search(project, "retry", ProjectDocsSearch.KindMemory);
        var adr = search.Search(project, "Ops cost", ProjectDocsSearch.KindAdr);

        Assert.Contains(memory.Matches, m => m.Kind == ProjectDocsSearch.KindMemory);
        Assert.DoesNotContain(memory.Matches, m => m.Kind == ProjectDocsSearch.KindAdr);
        Assert.All(adr.Matches, m => Assert.Equal(ProjectDocsSearch.KindAdr, m.Kind));
    }
}
