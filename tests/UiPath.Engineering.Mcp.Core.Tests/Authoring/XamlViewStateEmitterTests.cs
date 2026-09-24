using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Studio's designer state: a per-type sequential IdRef (which validate/build
/// diagnostics use as the activity handle), a HintSize, and an IsExpanded
/// ViewState on Sequences. Without them Studio re-lays-out the whole file on
/// save and diagnostics fall back to DisplayName/line matching.
/// </summary>
public class XamlViewStateEmitterTests {
    private static XDocument Doc(string xaml) => XDocument.Parse(xaml);

    private const string TwoOfAType = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence DisplayName="One">
            <ui:LogMessage DisplayName="A" />
          </Sequence>
          <Sequence DisplayName="Two">
            <ui:LogMessage DisplayName="B" />
          </Sequence>
        </Activity>
        """;

    [Fact]
    public void Apply_AssignsSequentialIdRefsPerType() {
        var doc = Doc(TwoOfAType);

        XamlViewStateEmitter.Apply(doc);

        var ids = doc.Root!.DescendantsAndSelf()
            .Select(e => e.Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef")?.Value)
            .Where(v => v is not null)
            .ToList();
        Assert.Contains("Sequence_1", ids);
        Assert.Contains("Sequence_2", ids);
        Assert.Contains("LogMessage_1", ids);
        Assert.Contains("LogMessage_2", ids);
    }

    [Fact]
    public void Apply_AddsHintSizeToEveryActivity() {
        var doc = Doc(TwoOfAType);

        XamlViewStateEmitter.Apply(doc);

        var activities = doc.Root!.DescendantsAndSelf()
            .Where(e => e.Name.LocalName is "Sequence" or "LogMessage")
            .ToList();
        Assert.NotEmpty(activities);
        Assert.All(activities, e =>
            Assert.NotNull(e.Attribute(XamlViewStateEmitter.Sap + "VirtualizedContainerService.HintSize")));
    }

    [Fact]
    public void Apply_DoesNotTouchViewStateInfrastructure() {
        var doc = Doc(TwoOfAType);

        XamlViewStateEmitter.Apply(doc);

        // The ViewState dictionary/Boolean must not themselves get an IdRef.
        var dictionary = doc.Root!.Descendants().First(e => e.Name.LocalName == "Dictionary");
        Assert.Null(dictionary.Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef"));
    }

    [Fact]
    public void Apply_SequenceGetsIsExpandedViewState() {
        var doc = Doc(TwoOfAType);

        XamlViewStateEmitter.Apply(doc);

        var sequence = doc.Root!.Elements().First(e => e.Name.LocalName == "Sequence");
        var viewState = sequence.Element(XamlViewStateEmitter.Sap + "WorkflowViewStateService.ViewState");
        Assert.NotNull(viewState);
        var boolean = viewState!.Descendants().Single(e => e.Name.LocalName == "Boolean");
        Assert.Equal("IsExpanded", boolean.Attribute(XamlViewStateEmitter.X + "Key")!.Value);
        Assert.Equal("True", boolean.Value);
    }

    [Fact]
    public void Apply_NonSequence_DoesNotGetViewStateDictionary() {
        var doc = Doc(TwoOfAType);

        XamlViewStateEmitter.Apply(doc);

        var logMessage = doc.Root!.Descendants().First(e => e.Name.LocalName == "LogMessage");
        Assert.Null(logMessage.Element(XamlViewStateEmitter.Sap + "WorkflowViewStateService.ViewState"));
    }

    [Fact]
    public void Apply_PreservesExistingIdRef() {
        var doc = Doc("""
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence sap2010:WorkflowViewState.IdRef="Sequence_7" />
            </Activity>
            """);

        XamlViewStateEmitter.Apply(doc);

        Assert.Equal("Sequence_7",
            doc.Root!.Descendants().Single(e => e.Name.LocalName == "Sequence")
                .Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef")!.Value);
    }

    [Fact]
    public void ApplyToFragment_ContinuesCountersFromTheTargetDocument() {
        var target = Doc("""
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence sap2010:WorkflowViewState.IdRef="Sequence_3" />
            </Activity>
            """);
        var fragment = XDocument.Parse("<Wrapper xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"><Sequence /></Wrapper>");

        XamlViewStateEmitter.ApplyToFragment(fragment.Root!.Elements().ToList(), target);

        Assert.Equal("Sequence_4",
            fragment.Root!.Elements().Single().Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef")!.Value);
    }

    [Fact]
    public void ApplyToFragment_LegacyDocument_UsesMscorlib() {
        var target = Doc("""
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:s="clr-namespace:System;assembly=mscorlib">
              <Sequence />
            </Activity>
            """);
        var fragment = XDocument.Parse("<Wrapper xmlns=\"http://schemas.microsoft.com/netfx/2009/xaml/activities\"><Sequence /></Wrapper>");

        XamlViewStateEmitter.ApplyToFragment(fragment.Root!.Elements().ToList(), target);

        var dictionary = fragment.Root!.Elements().Single()
            .Descendants().Single(e => e.Name.LocalName == "Dictionary");
        Assert.Contains("assembly=mscorlib", dictionary.Name.NamespaceName);
    }
}