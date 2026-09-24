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

    // The identity index used to exist only on this static catalog: ListActivityCatalog
    // — which backs ActivityCatalog.Fallback and every Merged catalog, and is what
    // get_activity_metadata and validate_activity_spec actually hold — indexed by
    // spec name alone. Callers there hold the emitted element name, so they got
    // ACTIVITY_NOT_FOUND for activities the catalog contains.
    [Theory]
    [InlineData("InterruptibleWhile")]
    [InlineData("InterruptibleDoWhile")]
    public void ListActivityCatalog_ResolvesTheEmittedElementName(string emitted) =>
        Assert.True(new ListActivityCatalog(ActivityCatalog.All, "test").TryGet(emitted, out _));

    [Theory]
    [InlineData("InterruptibleWhile")]
    [InlineData("InterruptibleDoWhile")]
    public void Fallback_ResolvesTheEmittedElementName(string emitted) {
        Assert.True(ActivityCatalog.Fallback.TryGet(emitted, out var viaCatalog));
        Assert.True(ActivityCatalog.TryGet(emitted, out var viaStatic));
        Assert.Equal(viaStatic!.Name, viaCatalog!.Name);
    }

    // The short UIA alias is deliberately NOT on the catalog index. XamlCatalogGuard
    // resolves XAML element names through it with namespace-blind local-name matching,
    // so indexing the bare "Click" would make ui:Click (legacy) and the mobile Click
    // look like the modern uix:NClick. It stays a launcher convenience on the static
    // TryGet, where the caller supplies its own namespace.
    [Theory]
    [InlineData("Click")]
    [InlineData("TypeInto")]
    public void ListActivityCatalog_DoesNotResolveTheShortUiaAlias(string alias) =>
        Assert.False(new ListActivityCatalog(ActivityCatalog.All, "test").TryGet(alias, out _));

    [Theory]
    [InlineData("Click", "NClick")]
    [InlineData("TypeInto", "NTypeInto")]
    public void StaticCatalog_StillResolvesTheShortUiaAliasForLaunching(string alias, string expected) {
        Assert.True(ActivityCatalog.TryGet(alias, out var schema));
        Assert.Equal(expected, schema!.Name);
    }
}
