using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Rule 20 structure-first: every node is a direct child of Flowchart /
/// StateMachine and wired with x:Reference, never nested inside the previous
/// node's Next. Nested nodes never enter Flowchart.Nodes, so the designer draws
/// nothing. ShapeLocation + ShapeSize are mandatory on every node.
/// </summary>
public class FlowchartStateMachineTests {
    private static ActivitySpec LeafActivity(string name) =>
        new() { Name = name, Properties = new() { ["displayName"] = name } };

    private static ActivitySpec Flowchart(params FlowNodeSpec[] nodes) =>
        new() {
            Name = "Flowchart",
            Flowchart = new FlowchartSpec {
                Start = nodes[0].Id,
                Nodes = [.. nodes]
            }
        };

    [Fact]
    public void Flowchart_RendersNodesAsDirectChildrenWiredByReference() {
        var spec = Flowchart(
            new FlowNodeSpec {
                Id = "1",
                Type = "Step",
                DisplayName = "First",
                Next = "2",
                Activity = LeafActivity("WriteLine"),
                ShapeLocation = "170,110",
                ShapeSize = "262,60"
            },
            new FlowNodeSpec {
                Id = "2",
                Type = "Decision",
                DisplayName = "Branch?",
                Condition = "[flag]",
                True = "1",
                ShapeLocation = "170,220",
                ShapeSize = "61,61"
            });

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        var doc = System.Xml.Linq.XDocument.Parse(result.Xaml!);
        var root = doc.Root!;
        var flowStep = root.Elements().Single(e => e.Name.LocalName == "FlowStep");
        var flowDecision = root.Elements().Single(e => e.Name.LocalName == "FlowDecision");

        // Both nodes are direct children (siblings) of Flowchart, not nested.
        Assert.Equal("__ReferenceID1", flowStep.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value);
        Assert.Equal("__ReferenceID2", flowDecision.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))!.Value);
        Assert.Equal("Flowchart", flowStep.Parent!.Name.LocalName);
        Assert.Equal("Flowchart", flowDecision.Parent!.Name.LocalName);

        // Wiring is by x:Reference, not nesting.
        var start = root.Element(root.Name.Namespace + "Flowchart.StartNode");
        Assert.NotNull(start);
        Assert.Equal("Reference", start!.Elements().Single().Name.LocalName);
        Assert.Equal("__ReferenceID1", start.Elements().Single().Value);
        Assert.NotNull(flowStep.Element(flowStep.Name.Namespace + "FlowStep.Next"));
        Assert.NotNull(flowDecision.Element(flowDecision.Name.Namespace + "FlowDecision.True"));
        // The FlowStep must not contain the FlowDecision.
        Assert.Empty(flowStep.Descendants().Where(e => e.Name.LocalName == "FlowDecision"));
    }

    [Fact]
    public void Flowchart_EveryNodeCarriesShapeLocationAndSize() {
        var spec = Flowchart(
            new FlowNodeSpec { Id = "1", Type = "Step", Next = "2", Activity = LeafActivity("WriteLine"), ShapeLocation = "10,20", ShapeSize = "100,50" },
            new FlowNodeSpec { Id = "2", Type = "Step", DisplayName = "End", ShapeLocation = "10,90", ShapeSize = "100,50" });

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("10,20", result.Xaml);
        Assert.Contains("100,50", result.Xaml);
        Assert.Contains("xmlns:av=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"", result.Xaml);
        var doc = System.Xml.Linq.XDocument.Parse(result.Xaml!);
        var av = System.Xml.Linq.XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        Assert.All(
            doc.Root!.Elements().Where(e => e.Name.LocalName is "FlowStep" or "FlowDecision"),
            node => {
                Assert.NotNull(node.Descendants(av + "Point").SingleOrDefault(p => p.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "ShapeLocation"));
                Assert.NotNull(node.Descendants(av + "Size").SingleOrDefault(p => p.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))?.Value == "ShapeSize"));
            });
    }

    [Fact]
    public void Validate_FlowchartOrphanNode_IsRejected() {
        var spec = Flowchart(
            new FlowNodeSpec { Id = "1", Type = "Step", Next = "2", Activity = LeafActivity("WriteLine") },
            // node 3 is declared but nothing links to it
            new FlowNodeSpec { Id = "2", Type = "Step", DisplayName = "End" },
            new FlowNodeSpec { Id = "3", Type = "Step", DisplayName = "Orphan" });

        var error = Assert.Single(SpecValidator.Validate(spec), e => e.Message.Contains("orphan"));
        Assert.Contains("3", error.Message);
    }

    [Fact]
    public void Validate_FlowchartLinkToUndeclaredNode_IsRejected() {
        var spec = Flowchart(
            new FlowNodeSpec { Id = "1", Type = "Step", Next = "99", Activity = LeafActivity("WriteLine") });

        Assert.Contains(SpecValidator.Validate(spec), e => e.Message.Contains("\"99\""));
    }

    [Fact]
    public void Validate_FlowchartMissingStart_IsRejected() {
        var spec = new ActivitySpec {
            Name = "Flowchart",
            Flowchart = new FlowchartSpec {
                Nodes = [new FlowNodeSpec { Id = "1", Type = "Step", DisplayName = "Only" }]
            }
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.Message.Contains("start"));
    }

    [Fact]
    public void Validate_FlowchartOnNonFlowchart_IsRejected() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Flowchart = new FlowchartSpec { Start = "1", Nodes = [new FlowNodeSpec { Id = "1" }] }
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
    }

    [Fact]
    public void StateMachine_RendersStatesAsDirectChildrenWithTransitionsByReference() {
        var spec = new ActivitySpec {
            Name = "StateMachine",
            StateMachine = new StateMachineSpec {
                Start = "1",
                States = [
                    new StateSpec {
                        Id = "1",
                        DisplayName = "Idle",
                        Activities = [LeafActivity("WriteLine")],
                        ShapeLocation = "10,10",
                        ShapeSize = "100,50",
                        Transitions = [new StateTransitionSpec { DisplayName = "go", To = "2", Condition = "[ready]" }]
                    },
                    new StateSpec {
                        Id = "2",
                        DisplayName = "Done",
                        Kind = "FinalState",
                        ShapeLocation = "10,90",
                        ShapeSize = "60,60"
                    }
                ]
            }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        var doc = System.Xml.Linq.XDocument.Parse(result.Xaml!);
        var root = doc.Root!;
        var state = root.Elements().Single(e => e.Name.LocalName == "State");
        var finalState = root.Elements().Single(e => e.Name.LocalName == "FinalState");
        Assert.Equal("StateMachine", state.Parent!.Name.LocalName);
        Assert.Equal("StateMachine", finalState.Parent!.Name.LocalName);
        Assert.Equal("{x:Reference __ReferenceID1}", root.Attribute("InitialState")!.Value);
        Assert.NotNull(state.Elements().Single(e => e.Name.LocalName == "Transition")
            .Element(state.Name.Namespace + "Transition.To"));
        Assert.Contains("ShapeLocation", result.Xaml);
        Assert.Contains("ShapeSize", result.Xaml);
    }

    [Fact]
    public void Validate_StateMachineOrphanState_IsRejected() {
        var spec = new ActivitySpec {
            Name = "StateMachine",
            StateMachine = new StateMachineSpec {
                Start = "1",
                States = [
                    new StateSpec { Id = "1", DisplayName = "One" },
                    new StateSpec { Id = "2", DisplayName = "Two" },
                    new StateSpec { Id = "3", DisplayName = "Orphan" }
                ]
            }
        };

        // State 2 and 3 are both unreferenced; the error names at least one.
        var orphans = SpecValidator.Validate(spec).Where(e => e.Message.Contains("orphan")).ToList();
        Assert.Equal(2, orphans.Count);
    }

    [Fact]
    public void Validate_TransitionWithoutTo_IsRejected() {
        var spec = new ActivitySpec {
            Name = "StateMachine",
            StateMachine = new StateMachineSpec {
                Start = "1",
                States = [new StateSpec { Id = "1", DisplayName = "One", Transitions = [new StateTransitionSpec()] }]
            }
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.Message.Contains("\"to\""));
    }

    [Fact]
    public void Catalog_ExposesDiagramActivities() {
        foreach (var name in new[] { "Flowchart", "FlowStep", "FlowDecision", "FlowSwitch", "StateMachine", "State", "Transition" }) {
            Assert.True(ActivityCatalog.TryGet(name, out _), $"{name} missing from the catalog");
        }
    }
}