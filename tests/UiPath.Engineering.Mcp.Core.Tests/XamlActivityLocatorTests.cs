using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class XamlActivityLocatorTests {
    private const string MixedXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main Sequence">
            <Sequence.Variables>
              <Variable x:TypeArguments="x:String" Name="userName" />
            </Sequence.Variables>
            <If DisplayName="If connected">
              <If.Then>
                <ui:LogMessage DisplayName="Log yes" Message="y" />
              </If.Then>
            </If>
            <ui:LogMessage DisplayName="Log done" Message="d" />
          </Sequence>
        </Activity>
        """;

    private const string LinesXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="First" />
            <WriteLine DisplayName="Second" />
          </Sequence>
        </Activity>
        """;

    private static IReadOnlyList<LocatedActivity> Locate(string xaml, LoadOptions options = LoadOptions.None) =>
        XamlActivityLocator.Locate(XDocument.Parse(xaml, options));

    [Fact]
    public void Locate_AssignsStructuralPathIds() {
        var activities = Locate(MixedXaml);

        Assert.Equal(
            ["sequence.1", "sequence.1/if.1", "sequence.1/if.1/logmessage.1", "sequence.1/logmessage.2"],
            activities.Select(a => a.Id).ToArray());
    }

    [Fact]
    public void Locate_OrdinalCountsAllActivitySiblingsNotPerName() {
        // If then LogMessage under the same Sequence: if.1 and logmessage.2 (not logmessage.1).
        var activities = Locate(MixedXaml);

        Assert.Equal("sequence.1/logmessage.2", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log done").Id);
    }

    [Fact]
    public void Locate_IfThenAndElse_DoNotShareIds() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Sequence>
                <If DisplayName="If connected">
                  <If.Then>
                    <ui:LogMessage DisplayName="Log then" Message="t" />
                  </If.Then>
                  <If.Else>
                    <ui:LogMessage DisplayName="Log else" Message="e" />
                  </If.Else>
                </If>
              </Sequence>
            </Activity>
            """;

        var activities = Locate(xaml);
        var ids = activities.Select(a => a.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("sequence.1/if.1/logmessage.1", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log then").Id);
        Assert.Equal("sequence.1/if.1/logmessage.2", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log else").Id);
    }

    [Fact]
    public void Locate_TreatsAttachedPropertyContainersAsTransparent() {
        var activities = Locate(MixedXaml);

        // Log yes lives under If.Then (transparent): parent is the If, depth 2, ordinal 1 of the If.Then child list.
        var logYes = activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log yes");
        Assert.Equal("sequence.1/if.1", logYes.ParentId);
        Assert.Equal(2, logYes.Depth);
        // Variables under Sequence.Variables never appear.
        Assert.DoesNotContain(activities, a => a.Element.Name.LocalName is "Variable" or "If.Then" or "Sequence.Variables");
    }

    [Fact]
    public void Locate_IsDeterministicAcrossParses() {
        var first = Locate(MixedXaml).Select(a => a.Id).ToArray();
        var second = Locate(MixedXaml).Select(a => a.Id).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Locate_ReportsOneBasedLineNumbersWhenLineInfoLoaded() {
        var activities = Locate(LinesXaml, LoadOptions.SetLineInfo);

        Assert.Equal(2, activities.Single(a => a.Id == "sequence.1").Line);
        Assert.Equal(3, activities.Single(a => a.Id == "sequence.1/writeline.1").Line);
        Assert.Equal(4, activities.Single(a => a.Id == "sequence.1/writeline.2").Line);
    }

    [Fact]
    public void Locate_ReportsZeroLineWhenLineInfoNotLoaded() {
        var activities = Locate(LinesXaml);

        Assert.All(activities, a => Assert.Equal(0, a.Line));
    }

    [Fact]
    public void Locate_ReadsWorkflowViewStateIdRef() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence sap2010:WorkflowViewState.IdRef="Sequence_1">
                <ui:LogMessage DisplayName="Log start" sap2010:WorkflowViewState.IdRef="LogMessage_1" />
              </Sequence>
            </Activity>
            """;

        var activities = Locate(xaml);
        Assert.Equal("Sequence_1", activities.Single(a => a.Id == "sequence.1").IdRef);
        Assert.Equal("LogMessage_1", activities.Single(a => a.Id == "sequence.1/logmessage.1").IdRef);
    }

    [Fact]
    public void Locate_PreservesSlotIdentityForBranchBodies() {
        var activities = Locate(MixedXaml);

        // Log yes sits in If.Then, so its slot is "Then"; its structural ID and
        // depth are unchanged (the slot is additive).
        var logYes = activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log yes");
        Assert.Equal("Then", logYes.Slot);
        Assert.Equal("sequence.1/if.1/logmessage.1", logYes.Id);
        // Log done is a plain child of the Sequence, so it has no slot.
        Assert.Null(activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Log done").Slot);
    }

    [Fact]
    public void Locate_DistinguishesWrappedFromBareThenBranch() {
        const string bare = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <If>
                <If.Then>
                  <ui:LogMessage DisplayName="Log then" />
                </If.Then>
              </If>
            </Activity>
            """;
        const string wrapped = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <If>
                <If.Then>
                  <Sequence DisplayName="Then">
                    <ui:LogMessage DisplayName="Log then" />
                  </Sequence>
                </If.Then>
              </If>
            </Activity>
            """;

        var bareThen = Locate(bare).Single(a => a.Element.Attribute("DisplayName")?.Value == "Log then");
        var wrappedThen = Locate(wrapped).Single(a => a.Element.Attribute("DisplayName")?.Value == "Log then");

        // The bare LogMessage is the Then child; the wrapped one sits inside a
        // Sequence, so it has no slot and a deeper depth.
        Assert.Equal("Then", bareThen.Slot);
        Assert.Null(wrappedThen.Slot);
        Assert.Equal(bareThen.Depth, wrappedThen.Depth - 1);
    }

    [Fact]
    public void Locate_ReportsTryCatchAndForEachSlots() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TryCatch>
                <TryCatch.Try>
                  <Sequence DisplayName="Try">
                    <ui:LogMessage DisplayName="In try" />
                  </Sequence>
                </TryCatch.Try>
                <TryCatch.Catches>
                  <Catch x:TypeArguments="s:Exception">
                    <ActivityAction x:TypeArguments="s:Exception">
                      <ActivityAction.Argument>
                        <DelegateInArgument x:TypeArguments="s:Exception" Name="ex" />
                      </ActivityAction.Argument>
                      <Sequence DisplayName="Catch">
                        <ui:LogMessage DisplayName="In catch" />
                      </Sequence>
                    </ActivityAction>
                  </Catch>
                </TryCatch.Catches>
              </TryCatch>
            </Activity>
            """;

        var activities = Locate(xaml);

        var trySeq = activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Try");
        Assert.Equal("Try", trySeq.Slot);
        // The Catch action's Sequence is the Catch body, carried through the
        // transparent ActivityAction wrapper.
        var catchSeq = activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Catch");
        Assert.Equal("Catch", catchSeq.Slot);
    }

    [Fact]
    public void Locate_ReportsForEachBodySlot() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ui:ForEach x:TypeArguments="x:String">
                <ui:ForEach.Body>
                  <ActivityAction x:TypeArguments="x:String">
                    <Sequence DisplayName="Body">
                      <ui:LogMessage DisplayName="In body" />
                    </Sequence>
                  </ActivityAction>
                </ui:ForEach.Body>
              </ui:ForEach>
            </Activity>
            """;

        var activities = Locate(xaml);

        Assert.Equal("Body", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Body").Slot);
    }

    [Fact]
    public void Locate_ReportsSwitchCaseSlots() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Switch x:TypeArguments="x:Int32">
                <Sequence x:Key="1" DisplayName="Case one">
                  <ui:LogMessage DisplayName="In case" />
                </Sequence>
                <Switch.Default>
                  <Sequence DisplayName="Default">
                    <ui:LogMessage DisplayName="In default" />
                  </Sequence>
                </Switch.Default>
              </Switch>
            </Activity>
            """;

        var activities = Locate(xaml);

        Assert.Equal("Case:1", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Case one").Slot);
        Assert.Equal("Default", activities.Single(a => a.Element.Attribute("DisplayName")?.Value == "Default").Slot);
    }

    [Fact]
    public void Locate_IgnoresViewStateDictionaryValues() {
        // Studio's designer state lives in a ViewState dictionary whose children are
        // keyed Av values (Point/Size/PointCollection). None is an activity, so none
        // receives a structural path.
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:av="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:sap="http://schemas.microsoft.com/netfx/2009/xaml/activities/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <FlowStep x:Name="__ReferenceID0">
                <sap:WorkflowViewStateService.ViewState>
                  <av:Point x:Key="ShapeLocation">170,110</av:Point>
                  <av:Size x:Key="ShapeSize">262,60</av:Size>
                </sap:WorkflowViewStateService.ViewState>
              </FlowStep>
            </Activity>
            """;

        var activities = Locate(xaml);

        Assert.DoesNotContain(activities, a => a.Element.Name.LocalName is "Point" or "Size" or "ViewState");
        Assert.Equal("flowstep.1", Assert.Single(activities).Id);
    }

    [Fact]
    public void Locate_AssignsPreOrderDocumentOrderIndex() {
        var activities = Locate(MixedXaml);

        Assert.Equal(Enumerable.Range(0, activities.Count).ToArray(), activities.Select(a => a.Order).ToArray());
        Assert.Null(activities[0].ParentId);
    }
}
