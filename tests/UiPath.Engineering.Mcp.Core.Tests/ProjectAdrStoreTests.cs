using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectAdrStoreTests {
    private readonly FakeFilesystemProvider _fs = new();
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mcp-adr-" + Guid.NewGuid().ToString("N"));

    private static string CompleteAdr(string title) =>
        ProjectAdrStore.RenderTemplate(title, AdrRecord.Accepted, "Need a durable queue.", "Use Orchestrator queues.", "More moving parts.");

    [Fact]
    public void Write_AutoNumbersAndIndexes() {
        var store = new ProjectAdrStore(_fs);

        var first = store.Write(_project, "Use queues", CompleteAdr("Use queues"), ["Main.xaml"], AdrRecord.Accepted, supersedes: null);
        var second = store.Write(_project, "Use assets", CompleteAdr("Use assets"), null, AdrRecord.Proposed, supersedes: null);

        Assert.Equal("0001-use-queues", first.Id);
        Assert.Equal("0002-use-assets", second.Id);
        Assert.Equal(2, store.Load(_project).Adrs.Count);
        Assert.True(_fs.FileExists(ProjectDocsPaths.AdrFile(_project, first.FileName)));
    }

    [Fact]
    public void Write_SupersedesMarksOldAdr() {
        var store = new ProjectAdrStore(_fs);
        var first = store.Write(_project, "Use queues", CompleteAdr("Use queues"), null, AdrRecord.Accepted, null);

        var next = store.Write(_project, "Use bus", CompleteAdr("Use bus"), null, AdrRecord.Accepted, supersedes: first.Id);

        var index = store.Load(_project);
        Assert.Equal(AdrRecord.Superseded, index.Adrs.Single(a => a.Id == first.Id).Status);
        Assert.Equal(first.Id, next.Supersedes);
        Assert.Contains("Status: superseded", _fs.ReadAllText(ProjectDocsPaths.AdrFile(_project, first.FileName)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_IncompleteTemplate_Throws() {
        var store = new ProjectAdrStore(_fs);
        Assert.Throws<ArgumentException>(() => store.Write(_project, "Incomplete", "# Incomplete\n\nNo sections.", null, null, null));
    }
}
