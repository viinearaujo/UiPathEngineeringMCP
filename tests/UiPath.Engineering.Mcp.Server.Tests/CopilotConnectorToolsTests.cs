using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class CopilotConnectorToolsTests {
    [Fact]
    public void DefaultNames_IsAtMostTwelveAndIncludesRecommendActivities() {
        Assert.Equal(CopilotConnectorTools.MaxDefaultCount, CopilotConnectorTools.DefaultNames.Length);
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
        Assert.Equal(CopilotConnectorTools.DefaultNames, CopilotConnectorTools.FilterNames(mixed));
    }

    [Fact]
    public void RestrictsSurface_OnlyAllDisablesTheFilter() {
        Assert.True(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceCopilotDefault));
        Assert.False(CopilotConnectorTools.RestrictsSurface(CopilotConnectorTools.SurfaceAll));
    }
}
