using System.Text.Json.Nodes;
using UiPath.Engineering.Mcp.Core.Canvas;

namespace UiPath.Engineering.Mcp.Core.Tests.Canvas;

public class CanvasSnapshotDocumentTests {
    [Fact]
    public void PriorityOrder_EntryFirst_ThenChildrenById_ThenRemainder() {
        var root = JsonNode.Parse("""
            {
              "schemaVersion": 1,
              "project": { "entryPoint": "Entry.xaml", "overview": "  " },
              "nodes": [
                { "id": "Entry.xaml", "explanation": "done" },
                { "id": "B.xaml", "explanation": "" },
                { "id": "C.xaml", "explanation": "  " },
                { "id": "D.xaml", "explanation": "" },
                { "id": "E.xaml", "explanation": "" },
                { "id": "F.xaml", "explanation": "kept" }
              ],
              "edges": [
                { "sourceWorkflow": "Entry.xaml", "targetWorkflow": "C.xaml", "isResolved": true },
                { "sourceWorkflow": "Entry.xaml", "targetWorkflow": "B.xaml", "isResolved": true },
                { "sourceWorkflow": "B.xaml", "targetWorkflow": "D.xaml", "isResolved": true },
                { "sourceWorkflow": "Entry.xaml", "targetWorkflow": "Missing.xaml", "isResolved": false }
              ]
            }
            """)!.AsObject();

        Assert.Equal(
            ["Entry.xaml", "B.xaml", "C.xaml", "D.xaml", "E.xaml", "F.xaml"],
            CanvasSnapshotDocument.PriorityOrder(root));
        var status = CanvasSnapshotDocument.Status(root, written: false);
        Assert.False(status.Written);
        Assert.False(status.OverviewWritten);
        Assert.Equal(2, status.ExplainedCount);
        Assert.Equal(6, status.TotalCount);
        Assert.Equal(4, status.RemainingCount);
        Assert.Equal(["B.xaml", "C.xaml", "D.xaml"], status.NextNodeIds);
    }

    [Fact]
    public void PriorityOrder_WithoutEntry_QueuesRootsBeforeExpanding() {
        var root = JsonNode.Parse("""
            {
              "schemaVersion": 1,
              "project": { "entryPoint": null, "overview": "" },
              "nodes": [
                { "id": "C.xaml", "explanation": "" },
                { "id": "A.xaml", "explanation": "" },
                { "id": "B.xaml", "explanation": "" }
              ],
              "edges": [
                { "sourceWorkflow": "A.xaml", "targetWorkflow": "B.xaml", "isResolved": true }
              ]
            }
            """)!.AsObject();

        Assert.Equal(["A.xaml", "C.xaml", "B.xaml"], CanvasSnapshotDocument.PriorityOrder(root));
        Assert.Equal(3, CanvasSnapshotDocument.Status(root, false).NextNodeIds.Count);
    }

    [Fact]
    public void TryRead_RejectsBadVersionAndDuplicateIds() {
        Assert.False(CanvasSnapshotDocument.TryRead("{", out _, out var invalid));
        Assert.Contains("invalid JSON", invalid);
        Assert.False(CanvasSnapshotDocument.TryRead("""{"schemaVersion":"1","nodes":[]}""", out _, out var version));
        Assert.Contains("schemaVersion", version);
        Assert.False(CanvasSnapshotDocument.TryRead("""
            {"schemaVersion":1,"nodes":[{"id":"A.xaml"},{"id":"A.xaml"}]}
            """, out _, out var duplicate));
        Assert.Contains("duplicate id", duplicate);
    }
}
