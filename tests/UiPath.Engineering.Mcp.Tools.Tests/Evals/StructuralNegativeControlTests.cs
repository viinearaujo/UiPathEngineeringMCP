namespace UiPath.Engineering.Mcp.Tools.Tests.Evals;

/// <summary>
/// Negative controls for <see cref="Structural"/>. These feed the OLD broken
/// shapes through the new assertions and require them to FAIL, which is the
/// property the substring harness lacked: <c>xaml.Contains("&lt;ui:RetryScope")</c>
/// and <c>xaml.Contains("&lt;If.Then&gt;")</c> both passed on the shapes asserted
/// below. Without these controls a passing eval proves only that the emitter
/// still emits something, not that the assertion can tell correct XAML from
/// broken XAML.
/// </summary>
public class StructuralNegativeControlTests {
    // The pre-fix RetryScope shape: children dumped directly, no ActivityBody wrapper.
    private const string BareRetryScope = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <ui:RetryScope NumberOfRetries="3">
            <ui:LogMessage Message="[ex]" />
          </ui:RetryScope>
        </Activity>
        """;

    // The pre-fix If shape: branches hold their activity directly, no Rule 24 wrap.
    private const string UnwrappedIf = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <If Condition="[flag]">
            <If.Then>
              <ui:LogMessage Message="&quot;ok&quot;" />
            </If.Then>
            <If.Else>
              <ui:LogMessage Message="&quot;no&quot;" />
            </If.Else>
          </If>
        </Activity>
        """;

    // A Switch whose x:TypeArguments is the raw spec token, not the resolved x: primitive.
    private const string RawSwitchTypeToken = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Switch x:TypeArguments="Int32" Expression="[status]">
            <Sequence x:Key="1" />
          </Switch>
        </Activity>
        """;

    // A flowchart node with no ShapeLocation/ShapeSize, stacked at (0,0) in Studio.
    private const string DiagramNodeWithoutShape = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Flowchart>
            <Flowchart.StartNode>
              <x:Reference>__ReferenceID1</x:Reference>
            </Flowchart.StartNode>
            <FlowStep x:Name="__ReferenceID1" />
          </Flowchart>
        </Activity>
        """;

    // Assign in the non-generic object form: no x:TypeArguments, no typed To/Value.
    private const string NonGenericAssign = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Assign To="[counter]" Value="[counter + 1]" />
        </Activity>
        """;

    // An otherwise-valid VB workflow with no IdRef, so diagnostics cannot map to it.
    private const string MissingIdRef = """
        <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence>
            <ui:LogMessage Message="&quot;hi&quot;" />
          </Sequence>
        </Activity>
        """;

    [Fact]
    public void BareRetryScope_FailsTheUntypedActionBodyAssertion() {
        var structural = Structural.Parse(BareRetryScope);
        var retry = structural.Find(EvalNs.Ui, "RetryScope")!;

        structural.RequireUntypedActionBody(retry, "ActivityBody");

        Assert.Contains(structural.Failures, f => f.Contains("ActivityBody", StringComparison.Ordinal));
    }

    [Fact]
    public void UnwrappedIf_FailsTheRule24WrapAssertion() {
        var structural = Structural.Parse(UnwrappedIf);

        structural.RequireRule24Wraps();

        Assert.Contains(structural.Failures, f => f.Contains("<If.Then>", StringComparison.Ordinal));
        Assert.Contains(structural.Failures, f => f.Contains("<If.Else>", StringComparison.Ordinal));
    }

    [Fact]
    public void RawSwitchTypeToken_FailsTheResolvedTokenAssertion() {
        var structural = Structural.Parse(RawSwitchTypeToken);
        var switchElement = structural.Find(EvalNs.Wf, "Switch")!;

        structural.Require(switchElement.Attribute(EvalNs.X + "TypeArguments")?.Value == "x:Int32",
            $"<Switch> x:TypeArguments expected \"x:Int32\" but was \"{switchElement.Attribute(EvalNs.X + "TypeArguments")?.Value}\"");

        Assert.Contains(structural.Failures, f => f.Contains("x:Int32", StringComparison.Ordinal));
    }

    [Fact]
    public void DiagramNodeWithoutShape_FailsTheShapeAssertion() {
        var structural = Structural.Parse(DiagramNodeWithoutShape);

        structural.RequireDiagram(EvalNs.Wf, "Flowchart", "FlowStep");

        Assert.Contains(structural.Failures, f => f.Contains("ShapeLocation", StringComparison.Ordinal));
        Assert.Contains(structural.Failures, f => f.Contains("ShapeSize", StringComparison.Ordinal));
    }

    [Fact]
    public void NonGenericAssign_FailsTheTypedAssignAssertion() {
        var structural = Structural.Parse(NonGenericAssign);

        structural.RequireTypedAssign("x:Int32");

        Assert.Contains(structural.Failures, f => f.Contains("x:TypeArguments", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingIdRef_FailsTheIdRefAssertion() {
        var structural = Structural.Parse(MissingIdRef);

        structural.RequireIdRefsOnEveryActivity();

        Assert.Contains(structural.Failures, f => f.Contains("IdRef", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateIdRef_FailsTheIdRefAssertion() {
        const string xaml = """
            <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence sap2010:WorkflowViewState.IdRef="Sequence_1">
                <WriteLine sap2010:WorkflowViewState.IdRef="Sequence_1" />
              </Sequence>
            </Activity>
            """;

        var structural = Structural.Parse(xaml);

        structural.RequireIdRefsOnEveryActivity();

        Assert.Contains(structural.Failures, f => f.Contains("duplicate IdRef", StringComparison.Ordinal));
    }

    // The positive control: the correct shapes pass the same helpers, so the
    // failures above are the assertion working and not the assertion over-firing.
    [Fact]
    public void CorrectRetryScopeBody_PassesTheUntypedActionBodyAssertion() {
        const string xaml = """
            <Activity x:Class="X" xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <ui:RetryScope NumberOfRetries="3" sap2010:WorkflowViewState.IdRef="RetryScope_1">
                <ui:RetryScope.ActivityBody>
                  <ActivityAction>
                    <Sequence sap2010:WorkflowViewState.IdRef="Sequence_1">
                      <ui:LogMessage Message="[ex]" sap2010:WorkflowViewState.IdRef="LogMessage_1" />
                    </Sequence>
                  </ActivityAction>
                </ui:RetryScope.ActivityBody>
              </ui:RetryScope>
            </Activity>
            """;

        var structural = Structural.Parse(xaml);
        var retry = structural.Find(EvalNs.Ui, "RetryScope")!;

        structural.RequireUntypedActionBody(retry, "ActivityBody").RequireIdRefsOnEveryActivity();

        Assert.Empty(structural.Failures);
    }
}