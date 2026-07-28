using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class DependencyGraphBuilderTests
{
    private static WorkflowModel Wf(string fileName, params string[] targets) => new()
    {
        FileName = fileName,
        InvokeWorkflows = targets
            .Select(t => new InvokeWorkflowModel { SourceWorkflow = fileName, TargetWorkflow = t })
            .ToList()
    };

    [Fact]
    public void Build_LinearChain_ResolvesAllEdgesAndNoOrphansOrCycles()
    {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml", "B.xaml"), Wf("B.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.Equal(2, result.Edges.Count);
        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Cycles);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Build_DetectsCycle()
    {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml", "B.xaml"), Wf("B.xaml", "A.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var cycle = Assert.Single(result.Cycles);
        Assert.Contains("A.xaml", cycle);
        Assert.Contains("B.xaml", cycle);
    }

    [Fact]
    public void Build_FindsOrphanWorkflowsNotReachableFromMain()
    {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml"), Wf("Unused.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.Equal(["Unused.xaml"], result.Orphans);
    }

    [Fact]
    public void Build_UnresolvedTarget_ProducesUnresolvedEdgeWithoutThrowing()
    {
        var workflows = new[] { Wf("Main.xaml", "Missing.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var edge = Assert.Single(result.Edges);
        Assert.False(edge.IsResolved);
        Assert.Equal("Missing.xaml", edge.Target);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void Build_MatchesFileNamesCaseInsensitively()
    {
        var workflows = new[] { Wf("Main.xaml", "child.XAML"), Wf("Child.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Orphans);
    }
}
