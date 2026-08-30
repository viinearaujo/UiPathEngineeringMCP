using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class CopilotConnectorToolsTests {
    [Fact]
    public void DefaultNames_IsAtMostTwelveAndIncludesRecommendActivities() {
        Assert.Equal(CopilotConnectorTools.MaxDefaultCount, CopilotConnectorTools.DefaultNames.Length);
        Assert.True(CopilotConnectorTools.DefaultNames.Length <= CopilotConnectorTools.MaxDefaultCount);
        Assert.Contains("recommend_activities", CopilotConnectorTools.DefaultNames);
        Assert.Equal(CopilotConnectorTools.DefaultNames.Length, CopilotConnectorTools.DefaultNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void LeaveOffNames_AreNotOnTheDefaultConnector() {
        foreach (var name in CopilotConnectorTools.LeaveOffNames) {
            Assert.False(CopilotConnectorTools.IsDefault(name), name);
        }
    }

    [Fact]
    public void FilterNames_KeepsOnlyDefaultTools() {
        var mixed = CopilotConnectorTools.DefaultNames.Concat(CopilotConnectorTools.LeaveOffNames);
        var filtered = CopilotConnectorTools.FilterNames(mixed);
        Assert.Equal(CopilotConnectorTools.DefaultNames, filtered);
    }

    [Fact]
    public void RestrictsSurface_OnlyAllDisablesTheFilter() {
        Assert.True(CopilotConnectorTools.RestrictsSurface(null));
        Assert.True(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceCopilotDefault));
        Assert.True(CopilotConnectorTools.RestrictsSurface("unexpected"));
        Assert.False(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceAll));
        Assert.False(CopilotConnectorTools.RestrictsSurface("all"));
    }
}
