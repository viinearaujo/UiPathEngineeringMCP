using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

/// <summary>
/// Structural assertions that reveal a production defect. Each test asserts the
/// CORRECT behavior and is SKIPPED while the defect stands, so the gap is
/// machine-visible in the suite instead of living only in a report. Un-skip a
/// test once its defect is fixed and it becomes the regression guard.
///
/// These are deliberately skipped rather than written to assert the defect: a
/// test that pins broken behavior would have to be deleted to land the fix, and
/// deleting an assertion to get green is what this eval rebuild exists to prevent.
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
    /// FINDING — an av:Point / av:Size ViewState value is misclassified as an
    /// activity, so a correctly-built Flowchart is refused by the catalog guard.
    ///
    /// File/line: <c>XamlViewStateEmitter.cs:179-183</c> (<c>IsActivity</c>) treats
    /// every non-dotted element that is not in
    /// <c>XamlWorkflowParser.NonActivityElements</c> as an activity and assigns it an
    /// IdRef and HintSize. <c>XamlBuilder.cs:792-801</c> (<c>ShapeViewState</c>) writes
    /// the coordinate values as <c>&lt;av:Point&gt;</c> / <c>&lt;av:Size&gt;</c>, and
    /// <c>XamlWorkflowParser.cs:12-23</c> does not list "Point"/"Size", while
    /// <c>XamlActivityLocator.IsActivity</c> consults only that set. The emitted XAML
    /// therefore carries <c>sap2010:WorkflowViewState.IdRef="Point_1"</c> on a
    /// coordinate, <c>XamlCatalogGuard.FindUnknownActivities</c> reports "Point" and
    /// "Size" as unknown activities, and the NEXT <c>write_workflow_file</c> on that
    /// diagram is REFUSED. Every Flowchart / StateMachine file is affected.
    /// </summary>
    [Fact(Skip = "Production defect: av:Point/av:Size ViewState values are misclassified as activities (XamlViewStateEmitter.IsActivity + XamlWorkflowParser.NonActivityElements), so a built diagram is refused by XamlCatalogGuard.")]
    public void BuiltFlowchart_IsAcceptedByTheCatalogGuard() {
        var result = RenderFlowchart();
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));

        var unknown = XamlCatalogGuard.FindUnknownActivities(result.Xaml!, ActivityCatalog.Fallback);

        Assert.Empty(unknown);
    }

    /// <summary>
    /// The same misclassification makes the namespace-blind parser emit phantom
    /// "Point"/"Size" entries into <c>WorkflowModel.Activities</c>, so any analysis
    /// over a diagram workflow (activity counts, DisplayName lint, nesting depth)
    /// counts coordinates as activities.
    /// </summary>
    [Fact(Skip = "Production defect: XamlActivityLocator emits phantom Point/Size activities from a built diagram's ViewState coordinates into WorkflowModel.Activities.")]
    public void BuiltFlowchart_HasNoPhantomCoordinateActivities() {
        var result = RenderFlowchart();
        var parsed = new XamlWorkflowParser().Parse("Flowchart.xaml", "Flowchart.xaml", result.Xaml!);

        Assert.DoesNotContain(parsed.Activities, a => a.Type is "Point" or "Size");
    }
}