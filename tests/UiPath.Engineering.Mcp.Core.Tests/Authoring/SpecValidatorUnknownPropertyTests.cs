using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Known-property enforcement. A schema whose surface reflection read completely
/// rejects a property the schema does not know about — a typo must be an error, not
/// a silent passthrough attribute. A curated schema or a starter sample keeps
/// tolerating unknown keys (it under-lists on purpose, for forward compatibility
/// with newer activity packages) and the builder warns on the passthrough.
/// </summary>
public class SpecValidatorUnknownPropertyTests {
    private static IActivityCatalog CatalogWith(ActivitySchema schema) => new ListActivityCatalog([schema], "test");

    private static ActivitySchema Complete(params PropertySchema[] properties) =>
        new("Reflected", "ui", "http://schemas.uipath.com/workflow/activities", false,
            [new PropertySchema("DisplayName", false, PropertyKind.Literal), .. properties],
            PackageId: "UiPath.Some.Activities", PropertiesAreComplete: true);

    private static ActivitySchema Sample() =>
        new("Sampled", "ui", "http://schemas.uipath.com/workflow/activities", false,
            [new PropertySchema("DisplayName", false, PropertyKind.Literal)], PropertiesAreComplete: false);

    [Fact]
    public void CompleteSurface_UnknownProperty_IsRejected() {
        var catalog = CatalogWith(Complete(new PropertySchema("Message", true, PropertyKind.Expression)));
        var spec = new ActivitySpec {
            Name = "Reflected",
            Properties = new() { ["messge"] = "[msg]" }
        };

        var errors = SpecValidator.Validate(spec, catalog);

        var error = Assert.Single(errors, e => e.ErrorCode == ToolErrorCodes.SpecUnknownProperty);
        Assert.Contains("messge", error.Message, StringComparison.Ordinal);
        Assert.Contains("Message", error.FixHint, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteSurface_KnownProperty_IsAccepted() {
        var catalog = CatalogWith(Complete(new PropertySchema("Message", true, PropertyKind.Expression)));
        var spec = new ActivitySpec {
            Name = "Reflected",
            Properties = new() { ["Message"] = "[\"hello\"]" }
        };

        Assert.Empty(SpecValidator.Validate(spec, catalog));
    }

    [Fact]
    public void IncompleteSurface_UnknownProperty_IsToleratedForForwardCompatibility() {
        // A sample surface under-lists: a newer package may declare a property the
        // catalog has not seen, so an unknown key stays a passthrough (with a
        // builder warning) rather than a hard error.
        var catalog = CatalogWith(Sample());
        var spec = new ActivitySpec {
            Name = "Sampled",
            Properties = new() { ["SomeNewProperty"] = "1" }
        };

        Assert.Empty(SpecValidator.Validate(spec, catalog));

        var render = XamlBuilder.RenderFragment(spec, catalog);
        Assert.True(render.Success, string.Join(";", render.Errors.Select(e => e.Message)));
        Assert.Contains(render.Warnings, w => w.Contains("SomeNewProperty", StringComparison.Ordinal));
    }

    [Fact]
    public void CompleteSurface_UnknownPropertyWithoutACloseMatch_SuggestsTheDeclaredNames() {
        var catalog = CatalogWith(Complete(new PropertySchema("Message", true, PropertyKind.Expression)));
        var spec = new ActivitySpec {
            Name = "Reflected",
            Properties = new() { ["TotallyUnrelated"] = "1" }
        };

        var error = Assert.Single(SpecValidator.Validate(spec, catalog), e => e.ErrorCode == ToolErrorCodes.SpecUnknownProperty);
        Assert.Contains("Message", error.FixHint, StringComparison.Ordinal);
    }

    [Fact]
    public void CuratedCatalogProperty_StaysLenient() {
        // The built-in curated schemas deliberately under-list (a newer package
        // version may add properties), so the fallback catalog keeps tolerating an
        // unknown key — the behavior the pre-existing passthrough tests pinned.
        var spec = new ActivitySpec {
            Name = "Sequence",
            Properties = new() { ["ContinueOnError"] = "True" }
        };

        Assert.Empty(SpecValidator.Validate(spec));
    }
}