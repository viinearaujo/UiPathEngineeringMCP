using UiPath.Engineering.Mcp.Core.Safety;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectCheckpointServiceTests {
    [Fact]
    public void Checkpoint_GetChanges_Revert_RoundTripOnTempProject() {
        var root = Path.Combine(Path.GetTempPath(), "mcp-cp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "project.json"), """{"name":"demo"}""");
        var workflow = Path.Combine(root, "Main.xaml");
        File.WriteAllText(workflow, "<Activity DisplayName=\"v1\"/>");

        try {
            var journal = new ProjectWriteJournal();
            var service = new ProjectCheckpointService(journal);
            var created = service.Create(root);
            Assert.False(string.IsNullOrWhiteSpace(created.CheckpointId));
            Assert.Contains("Main.xaml", created.Manifest.Files);

            File.WriteAllText(workflow, "<Activity DisplayName=\"v2\"/>");
            var createdAfter = Path.Combine(root, "New.cs");
            File.WriteAllText(createdAfter, "class New {}");
            using (WriteJournalContext.Begin("add_coded_workflow", created.CheckpointId)) {
                journal.RecordWrite(createdAfter);
            }

            var changes = service.GetChanges(root, created.CheckpointId);
            Assert.Equal(created.CheckpointId, changes.CheckpointId);
            Assert.Contains("Main.xaml", changes.Diff);
            Assert.Contains("v1", changes.Diff);
            Assert.Contains("v2", changes.Diff);

            var restored = service.Revert(root, created.CheckpointId);
            Assert.True(restored >= 1);
            Assert.Equal("<Activity DisplayName=\"v1\"/>", File.ReadAllText(workflow));
            Assert.False(File.Exists(createdAfter));
        } finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
