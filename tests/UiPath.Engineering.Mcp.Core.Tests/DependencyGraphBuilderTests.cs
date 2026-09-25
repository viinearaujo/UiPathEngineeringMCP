using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UiPath.Engineering.Mcp.Core.CodeAnalysis;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class DependencyGraphBuilderTests {
    private static WorkflowModel Wf(string fileName, params string[] targets) => new() {
        FileName = fileName,
        RelativePath = fileName,
        InvokeWorkflows = targets
            .Select(t => new InvokeWorkflowModel { SourceWorkflow = fileName, TargetWorkflow = t })
            .ToList()
    };

    private static List<InvokeWorkflowModel> ScanCoded(string identity, string source) =>
        CodedWorkflowInvokeScanner.Scan(identity, source).ToList();


    [Fact]
    public void Build_LinearChain_ResolvesAllEdgesAndNoOrphansOrCycles() {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml", "B.xaml"), Wf("B.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.Equal(2, result.Edges.Count);
        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Cycles);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Build_DetectsCycle() {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml", "B.xaml"), Wf("B.xaml", "A.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var cycle = Assert.Single(result.Cycles);
        Assert.Contains("A.xaml", cycle);
        Assert.Contains("B.xaml", cycle);
    }

    [Fact]
    public void Build_FindsOrphanWorkflowsNotReachableFromMain() {
        var workflows = new[] { Wf("Main.xaml", "A.xaml"), Wf("A.xaml"), Wf("Unused.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.Equal(["Unused.xaml"], result.Orphans);
    }

    [Fact]
    public void Build_UnresolvedTarget_ProducesUnresolvedEdgeWithoutThrowing() {
        var workflows = new[] { Wf("Main.xaml", "Missing.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var edge = Assert.Single(result.Edges);
        Assert.False(edge.IsResolved);
        Assert.Equal("Missing.xaml", edge.Target);
        Assert.Empty(result.Cycles);
    }

    [Fact]
    public void Build_MatchesFileNamesCaseInsensitively() {
        var workflows = new[] { Wf("Main.xaml", "child.XAML"), Wf("Child.xaml") };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Build_EdgesCarryDisplayNameAndArgumentMappings() {
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "Main.xaml",
                    TargetWorkflow = "Child.xaml",
                    DisplayName = "Invoke child",
                    ArgumentMappings = [new ArgumentMappingModel {
                        Direction = "In", TargetArgument = "in_CustomerId", Expression = "[customerId]"
                    }]
                }]
            },
            new() { FileName = "Child.xaml" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var edge = Assert.Single(graph.Edges);
        Assert.Equal("Invoke child", edge.DisplayName);
        var mapping = Assert.Single(edge.ArgumentMappings);
        Assert.Equal("in_CustomerId", mapping.TargetArgument);
        Assert.Equal("[customerId]", mapping.Expression);
    }

    [Fact]
    public void Build_ResolvesFrameworkRelativeInvokeWithoutOrphan() {
        var workflows = new[] {
            new WorkflowModel {
                FileName = "Main.xaml",
                RelativePath = "Main.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "Main.xaml",
                    TargetWorkflow = @"Framework\InitAllSettings.xaml"
                }]
            },
            new WorkflowModel {
                FileName = "InitAllSettings.xaml",
                RelativePath = "Framework/InitAllSettings.xaml"
            }
        };

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Orphans);
        Assert.Equal("Framework/InitAllSettings.xaml", result.Edges[0].Target);
    }

    [Fact]
    public void Build_NormalizesDotSlashInvoke() {
        var workflows = new[] {
            Wf("Main.xaml", "./Child.xaml"),
            Wf("Child.xaml")
        };
        workflows[0].RelativePath = "Main.xaml";
        workflows[1].RelativePath = "Child.xaml";

        var result = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        Assert.All(result.Edges, e => Assert.True(e.IsResolved));
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Build_DuplicateBasenames_ResolveByRelativePathAndStayDistinct() {
        var workflows = new[] {
            new WorkflowModel {
                FileName = "Process.xaml",
                RelativePath = "A/Process.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel {
                    SourceWorkflow = "A/Process.xaml",
                    TargetWorkflow = "B/Process.xaml"
                }]
            },
            new WorkflowModel { FileName = "Process.xaml", RelativePath = "B/Process.xaml" }
        };

        var result = DependencyGraphBuilder.Build(workflows, "A/Process.xaml");

        var edge = Assert.Single(result.Edges);
        Assert.True(edge.IsResolved);
        Assert.Equal("A/Process.xaml", edge.Source);
        Assert.Equal("B/Process.xaml", edge.Target);
        Assert.Empty(result.Orphans);
    }

    [Fact]
    public void Build_CallersIndexMapsTargetToIncomingEdges() {
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.xaml",
                InvokeWorkflows = [
                    new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Child.xaml" },
                    new InvokeWorkflowModel { SourceWorkflow = "Main.xaml", TargetWorkflow = "Ghost.xaml" }
                ]
            },
            new() {
                FileName = "Other.xaml",
                InvokeWorkflows = [new InvokeWorkflowModel { SourceWorkflow = "Other.xaml", TargetWorkflow = "Child.xaml" }]
            },
            new() { FileName = "Child.xaml" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var childCallers = graph.CallersIndex["child.xaml"]; // case-insensitive
        Assert.Equal(2, childCallers.Count);
        Assert.Contains(childCallers, e => e.Source == "Main.xaml");
        Assert.Contains(childCallers, e => e.Source == "Other.xaml");
        // Unresolved targets are indexed too, so callers of missing workflows are visible.
        var ghostCallers = graph.CallersIndex["Ghost.xaml"];
        Assert.Single(ghostCallers);
        Assert.False(ghostCallers[0].IsResolved);
    }

    [Fact]
    public void Build_CodedRunWorkflow_CreatesResolvedEdgeWithMappings() {
        const string mainSource = """
            using System.Collections.Generic;
            public class Main : CodedWorkflow {
                [Workflow]
                public void Execute() {
                    var orderId = "O-1";
                    RunWorkflow("Child.cs", new Dictionary<string, object> { { "id", orderId } });
                }
            }
            public class CodedWorkflow { }
            public class WorkflowAttribute : System.Attribute { }
            """;
        var invokes = ScanCoded("Main.cs", mainSource);
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.cs",
                RelativePath = "Main.cs",
                InvokeWorkflows = invokes.ToList()
            },
            new() { FileName = "Child.cs", RelativePath = "Child.cs" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.cs");

        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsResolved);
        Assert.Equal("Main.cs", edge.Source);
        Assert.Equal("Child.cs", edge.Target);
        Assert.Equal("RunWorkflow", edge.DisplayName);
        var mapping = Assert.Single(edge.ArgumentMappings);
        Assert.Equal("In", mapping.Direction);
        Assert.Equal("id", mapping.TargetArgument);
        Assert.Equal("orderId", mapping.Expression);
    }

    [Fact]
    public void Build_CodedWorkflowsHelper_CreatesResolvedEdge() {
        const string mainSource = """
            public class Main : CodedWorkflow {
                WorkflowsHost workflows = new();
                [Workflow]
                public void Execute() {
                    workflows.Child(invoiceId: "INV-1");
                }
            }
            public class WorkflowsHost {
                public void Child(string invoiceId) { }
            }
            public class CodedWorkflow { }
            public class WorkflowAttribute : System.Attribute { }
            """;
        var invokes = ScanCoded("Main.cs", mainSource);
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.cs",
                RelativePath = "Main.cs",
                InvokeWorkflows = invokes.ToList()
            },
            new() { FileName = "Child.cs", RelativePath = "Child.cs" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.cs");

        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsResolved);
        Assert.Equal("Child.cs", edge.Target);
        Assert.Equal("workflows.Child", edge.DisplayName);
        var mapping = Assert.Single(edge.ArgumentMappings);
        Assert.Equal("invoiceId", mapping.TargetArgument);
        Assert.Equal("\"INV-1\"", mapping.Expression);
    }

    [Fact]
    public void Build_CodedUnresolvedHelper_ProducesUnresolvedEdge() {
        const string mainSource = """
            public class Main : CodedWorkflow {
                dynamic workflows = null!;
                [Workflow]
                public void Execute() {
                    workflows.Missing();
                }
            }
            public class CodedWorkflow { }
            public class WorkflowAttribute : System.Attribute { }
            """;
        var invokes = ScanCoded("Main.cs", mainSource);
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.cs",
                RelativePath = "Main.cs",
                InvokeWorkflows = invokes.ToList()
            }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.cs");

        var edge = Assert.Single(graph.Edges);
        Assert.False(edge.IsResolved);
        Assert.Equal("Missing.cs", edge.Target);
        Assert.Equal("workflows.Missing", edge.DisplayName);
    }

    [Fact]
    public void Build_XamlInvokeStillPresent_WhenCodedNodesExist() {
        var workflows = new List<WorkflowModel> {
            new() {
                FileName = "Main.xaml",
                RelativePath = "Main.xaml",
                InvokeWorkflows = [
                    new InvokeWorkflowModel {
                        SourceWorkflow = "Main.xaml",
                        TargetWorkflow = "Child.xaml",
                        DisplayName = "Invoke child"
                    }
                ]
            },
            new() { FileName = "Child.xaml", RelativePath = "Child.xaml" },
            new() { FileName = "Helper.cs", RelativePath = "Helper.cs" } // coded node, no edges
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.xaml");

        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsResolved);
        Assert.Equal("Main.xaml", edge.Source);
        Assert.Equal("Child.xaml", edge.Target);
        Assert.Equal("Invoke child", edge.DisplayName);
        Assert.Contains("Helper.cs", graph.Orphans);
    }

    [Fact]
    public void Build_CodedWorkflowWithNoCalls_IsNodeWithoutEdges() {
        var workflows = new[] {
            new WorkflowModel { FileName = "Main.cs", RelativePath = "Main.cs" },
            new WorkflowModel { FileName = "Lonely.cs", RelativePath = "Lonely.cs" }
        };

        var graph = DependencyGraphBuilder.Build(workflows, "Main.cs");

        Assert.Empty(graph.Edges);
        Assert.Equal(["Lonely.cs"], graph.Orphans);
    }

    [Fact]
    public void Build_PromotesCodedWorkflowsAndScansWithSyntaxOnlyAnalysis() {
        const string mainSource = """
            public class Main : CodedWorkflow {
                dynamic workflows = null!;
                [Workflow] public void Execute() { workflows.Child(); }
            }
            public class CodedWorkflow { }
            public class WorkflowAttribute : System.Attribute { }
            """;
        const string childSource = """
            public class Child : CodedWorkflow {
                [Workflow] public void Execute() { }
            }
            public class CodedWorkflow { }
            public class WorkflowAttribute : System.Attribute { }
            """;
        var treeMain = CSharpSyntaxTree.ParseText(mainSource, path: "Main.cs");
        var treeChild = CSharpSyntaxTree.ParseText(childSource, path: "Child.cs");
        var compilation = CSharpCompilation.Create(
            "coded-edges",
            [treeMain, treeChild],
            references: [],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var analysis = new CSharpAnalysisContext {
            Compilation = compilation,
            Mode = CSharpAnalysisMode.SyntaxOnly,
            HasCSharpFiles = true
        };
        var coded = new[] {
            new CodedWorkflowModel {
                FileName = "Main.cs", FilePath = "Main.cs", ClassName = "Main",
                Kind = CodedFileKind.Workflow, IsCodedWorkflow = true
            },
            new CodedWorkflowModel {
                FileName = "Child.cs", FilePath = "Child.cs", ClassName = "Child",
                Kind = CodedFileKind.Workflow, IsCodedWorkflow = true
            },
            new CodedWorkflowModel {
                FileName = "Util.cs", FilePath = "Util.cs", ClassName = "Util",
                Kind = CodedFileKind.Source // must not become a node
            }
        };

        var graph = DependencyGraphBuilder.Build([], "Main.cs", coded, analysis);

        var edge = Assert.Single(graph.Edges);
        Assert.True(edge.IsResolved);
        Assert.Equal("Main.cs", edge.Source);
        Assert.Equal("Child.cs", edge.Target);
        Assert.Equal("workflows.Child", edge.DisplayName);
        Assert.Empty(graph.Orphans);
        Assert.DoesNotContain(graph.Edges, e => e.Source.Contains("Util", StringComparison.OrdinalIgnoreCase)
            || e.Target.Contains("Util", StringComparison.OrdinalIgnoreCase));
    }
}
