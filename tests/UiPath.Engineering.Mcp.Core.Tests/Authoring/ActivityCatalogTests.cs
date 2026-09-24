using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ActivityCatalogTests {
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

    [Theory]
    [InlineData("While", "InterruptibleWhile", "UiPath.Core.Activities.InterruptibleWhile")]
    [InlineData("DoWhile", "InterruptibleDoWhile", "UiPath.Core.Activities.InterruptibleDoWhile")]
    [InlineData("ForEach", "ForEach", "UiPath.Core.Activities.ForEach`1")]
    public void LoopActivities_EmitTheUiPathWrapInTheUiPathCoreNamespace(string name, string element, string fullType) {
        Assert.True(ActivityCatalog.TryGet(name, out var schema));
        Assert.Equal(element, schema!.RenderName);
        Assert.Equal(fullType, schema.FullTypeName);
        Assert.Equal("http://schemas.uipath.com/workflow/activities", schema.XmlNamespace);
        Assert.Equal(ActivityCatalog.SystemPackage, schema.PackageId);
    }

    [Theory]
    [InlineData("InterruptibleWhile", "While")]
    [InlineData("InterruptibleDoWhile", "DoWhile")]
    public void LoopActivities_ResolveByBothTheToolboxLabelAndTheEmittedType(string emitted, string label) {
        Assert.True(ActivityCatalog.TryGet(emitted, out var byElement));
        Assert.True(ActivityCatalog.TryGet(label, out var byLabel));
        Assert.Equal(byLabel!.Name, byElement!.Name);
    }

    [Theory]
    [InlineData("ForEachRow", "UiPath.System.Activities")]
    [InlineData("ReadRange", "UiPath.Excel.Activities")]
    [InlineData("WriteRange", "UiPath.Excel.Activities")]
    [InlineData("LogMessage", "UiPath.System.Activities")]
    [InlineData("RetryScope", "UiPath.System.Activities")]
    [InlineData("InvokeCode", "UiPath.System.Activities")]
    [InlineData("ReadRangeX", "UiPath.Excel.Activities")]
    [InlineData("WriteRangeX", "UiPath.Excel.Activities")]
    [InlineData("ExcelApplicationCard", "UiPath.Excel.Activities")]
    [InlineData("NApplicationCard", "UiPath.UIAutomation.Activities")]
    [InlineData("NClick", "UiPath.UIAutomation.Activities")]
    public void Activities_AreStampedWithTheirOwningPackage(string name, string packageId) {
        Assert.True(ActivityCatalog.TryGet(name, out var schema));
        Assert.Equal(packageId, schema!.PackageId);
    }

    [Fact]
    public void ModernExcelActivities_UseTheBusinessClrNamespace() {
        Assert.True(ActivityCatalog.TryGet("ReadRangeX", out var schema));
        Assert.Equal("ueab", schema!.Prefix);
        Assert.Contains("clr-namespace:UiPath.Excel.Activities.Business", schema.XmlNamespace);
    }

    [Fact]
    public void UiaActivities_UseTheUixNamespace() {
        Assert.True(ActivityCatalog.TryGet("NClick", out var schema));
        Assert.Equal("uix", schema!.Prefix);
        Assert.Equal("http://schemas.uipath.com/workflow/activities/uix", schema.XmlNamespace);
        // The short toolbox name resolves too.
        Assert.True(ActivityCatalog.TryGet("Click", out var shortName));
        Assert.Equal("NClick", shortName!.Name);
    }
}
