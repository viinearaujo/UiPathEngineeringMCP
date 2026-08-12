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
    public void Locate_AssignsPreOrderDocumentOrderIndex() {
        var activities = Locate(MixedXaml);

        Assert.Equal(Enumerable.Range(0, activities.Count).ToArray(), activities.Select(a => a.Order).ToArray());
        Assert.Null(activities[0].ParentId);
    }
}
