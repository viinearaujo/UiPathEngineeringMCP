using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

/// <summary>
/// Structural regression guards for defects the structural eval harness exposed.
/// Each test asserts the CORRECT behavior, so the defect cannot silently return.
/// </summary>
public class ProductionFindingsTests {
    private static XamlBuildResult RenderFlowchart() =>
        XamlBuilder.RenderWorkflowFile(new ActivitySpec {
            Name = "Flowchart",
            Flowchart = new FlowchartSpec {
                Start = "1",
                Nodes = [
                    new FlowNodeSpec { Id = "1", Type = "Step", DisplayName = "First", Next = "2",
                        Activity = new ActivitySpec { Name = "WriteLine", Properties = new() { ["Text"] = "\"hi\"" } } },
                    new FlowNodeSpec { Id = "2", Type = "Step", DisplayName = "End" }
                ]
            }
        }, "FindingsFlowchart");

    /// <summary>
    /// REGRESSION GUARD — an av:Point / av:Size ViewState dictionary value is NOT an
    /// activity, so a correctly-built Flowchart is accepted by the catalog guard.
    ///
    /// <c>XamlViewStateEmitter.Apply</c> classified every non-dotted element that was
    /// not in <c>XamlWorkflowParser.NonActivityElements</c> as an activity and stamped
    /// it with an IdRef and HintSize. <c>XamlBuilder.ShapeViewState</c> writes the
    /// coordinates as <c>&lt;av:Point&gt;</c> / <c>&lt;av:Size&gt;</c> inside the
    /// node's <c>ViewState</c> dictionary, and those local names ("Point"/"Size") passed
    /// that filter. The emitted XAML therefore carried
    /// <c>sap2010:WorkflowViewState.IdRef="Point_1"</c> on a coordinate,
    /// <c>XamlCatalogGuard.FindUnknownActivities</c> reported "Point" and "Size" as
    /// unknown activities, and the NEXT <c>write_workflow_file</c> on that diagram was
    /// REFUSED. Every Flowchart / StateMachine file was affected.
    ///
    /// The fix makes designer state structurally opaque: the ViewState dictionary and
    /// every value inside it are excluded from activity classification by
    /// <c>XamlWorkflowParser.IsWithinViewState</c> / <c>IsViewStateDictionary</c>.
    /// </summary>
    [Fact]
    public void BuiltFlowchart_IsAcceptedByTheCatalogGuard() {
        var result = RenderFlowchart();
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));

        var unknown = XamlCatalogGuard.FindUnknownActivities(result.Xaml!, ActivityCatalog.Fallback);

        Assert.Empty(unknown);
    }

    /// <summary>
    /// REGRESSION GUARD — the same misclassification made the namespace-blind parser
    /// emit phantom "Point"/"Size" entries into <c>WorkflowModel.Activities</c>, so any
    /// analysis over a diagram workflow (activity counts, DisplayName lint, nesting
    /// depth) counted designer coordinates as activities. ViewState values are never
    /// activities, so the model carries no such phantom entries.
    /// </summary>
    [Fact]
    public void BuiltFlowchart_HasNoPhantomCoordinateActivities() {
        var result = RenderFlowchart();
        var parsed = new XamlWorkflowParser().Parse("Flowchart.xaml", "Flowchart.xaml", result.Xaml!);

        Assert.DoesNotContain(parsed.Activities, a => a.Type is "Point" or "Size");
    }
}