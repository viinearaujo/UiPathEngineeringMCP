using System.Xml.Linq;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class XamlActivityEditorViewStateTests {
    private const string Main = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="Start" Text="hi" sap2010:WorkflowViewState.IdRef="WriteLine_1"
              xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation" />
          </Sequence>
        </Activity>
        """;

    [Fact]
    public void Insert_AssignsIdRefHintSizeAndViewStateToTheInsertedFragment() {
        var result = XamlActivityEditor.Edit(Main, XamlActivityEditor.Insert, "Main",
            fragment: "<ui:LogMessage DisplayName=\"End\" Message=\"done\" xmlns:ui=\"http://schemas.uipath.com/workflow/activities\" />");

        Assert.True(result.Success, result.Error);
        var doc = XDocument.Parse(result.UpdatedContent!);
        var inserted = doc.Descendants().Single(e => e.Name.LocalName == "LogMessage");
        Assert.Equal("LogMessage_1",
            inserted.Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef")!.Value);
        Assert.NotNull(inserted.Attribute(XamlViewStateEmitter.Sap + "VirtualizedContainerService.HintSize"));
    }

    [Fact]
    public void Insert_ContinuesACounterAlreadyPresentInTheDocument() {
        var result = XamlActivityEditor.Edit(Main, XamlActivityEditor.Insert, "Main",
            fragment: "<WriteLine DisplayName=\"End\" Text=\"bye\" />");

        Assert.True(result.Success, result.Error);
        var doc = XDocument.Parse(result.UpdatedContent!);
        // WriteLine_1 already exists on Start, so the insert must not collide.
        Assert.Equal("WriteLine_2",
            doc.Descendants().Single(e => e.Attribute("DisplayName")?.Value == "End")
                .Attribute(XamlViewStateEmitter.Sap2010 + "WorkflowViewState.IdRef")!.Value);
    }
}