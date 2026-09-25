using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Safety;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectWriteJournalTests {
    [Fact]
    public void RecordWrite_AppendsJsonLine_AndIgnoresMcpPaths() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.json"), """{"name":"t"}""");
        var workflow = Path.Combine(root, "Main.xaml");
        File.WriteAllText(workflow, "<Activity/>");

        try {
            var journal = new ProjectWriteJournal();
            journal.SetActiveCheckpoint(root, "cp1");
            using (WriteJournalContext.Begin("edit_workflow_file")) {
                journal.RecordWrite(workflow);
                journal.RecordWrite(Path.Combine(root, ".mcp", "checkpoints", "x.json"));
            }

            var entries = journal.ReadEntries(root);
            Assert.Single(entries);
            Assert.Equal("edit_workflow_file", entries[0].Tool);
            Assert.Equal("Main.xaml", entries[0].RelativePath);
            Assert.Equal("cp1", entries[0].CheckpointId);

            var lines = File.ReadAllLines(Path.Combine(root, ".mcp", "journal.jsonl"));
            Assert.Single(lines);
            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("Main.xaml", doc.RootElement.GetProperty("relativePath").GetString());
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
