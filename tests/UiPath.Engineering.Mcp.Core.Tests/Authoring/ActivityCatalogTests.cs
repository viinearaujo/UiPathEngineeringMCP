using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ActivityCatalogTests
{
    [Theory]
    [InlineData("Sequence", true)]
    [InlineData("sequence", true)]   // case-insensitive
    [InlineData("Switch", true)]
    [InlineData("NotAnActivity", false)]
    public void TryGet_KnownAndUnknown(string name, bool expected) =>
        Assert.Equal(expected, ActivityCatalog.TryGet(name, out _));

    [Fact]
    public void Suggest_Typo_ReturnsClosestName() =>
        Assert.Equal("ForEach", ActivityCatalog.Suggest("FoeEach"));

    [Fact]
    public void All_RequiredPropertiesHaveName() =>
        Assert.All(ActivityCatalog.All, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
}
