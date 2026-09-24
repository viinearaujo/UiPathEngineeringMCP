using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// Assign renders the preferred generic Assign&lt;T&gt; form whenever the spec
/// declares a TypeArgument: the schema now carries that property, so it maps to
/// x:TypeArguments and to typed Assign.To / Assign.Value property elements.
/// </summary>
public class AssignTypeArgumentsTests {
    [Fact]
    public void Catalog_DeclaresATypeArgumentPropertyOnAssign() {
        Assert.True(ActivityCatalog.TryGet("Assign", out var schema));
        var typeArgument = Assert.Single(schema!.Properties, p => p.Name == "TypeArgument");

        Assert.Equal(PropertyKind.TypeArgument, typeArgument.Kind);
        Assert.False(typeArgument.Required);
    }

    [Fact]
    public void TypedAssign_RendersGenericFormWithTypedOutAndInArguments() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[counter]", ["value"] = "[counter + 1]", ["typeArgument"] = "Int32" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Assign x:TypeArguments=\"x:Int32\"", result.Xaml);
        Assert.Contains("<Assign.To>", result.Xaml);
        Assert.Contains("<OutArgument x:TypeArguments=\"x:Int32\">[counter]</OutArgument>", result.Xaml);
        Assert.Contains("<Assign.Value>", result.Xaml);
        Assert.Contains("<InArgument x:TypeArguments=\"x:Int32\">[counter + 1]</InArgument>", result.Xaml);
        // To is a write side, so it must never render as an InArgument.
        Assert.DoesNotContain("<InArgument x:TypeArguments=\"x:Int32\">[counter]</InArgument>", result.Xaml);
    }

    [Theory]
    [InlineData("DataTable", "sd:DataTable")]
    [InlineData("DateTime", "s:DateTime")]
    [InlineData("String", "x:String")]
    public void TypedAssign_ResolvesTheDeclaredTypeToAValidPrefix(string declared, string expected) {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[target]", ["value"] = "[source]", ["typeArgument"] = declared }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains($"<Assign x:TypeArguments=\"{expected}\"", result.Xaml);
        Assert.Contains($"<OutArgument x:TypeArguments=\"{expected}\">", result.Xaml);
        Assert.Contains($"<InArgument x:TypeArguments=\"{expected}\">", result.Xaml);
    }

    [Fact]
    public void UntypedAssign_KeepsTheNonGenericAttributeFormInVB() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[counter]", ["value"] = "[counter + 1]" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("To=\"[counter]\"", result.Xaml);
        Assert.Contains("Value=\"[counter + 1]\"", result.Xaml);
        Assert.DoesNotContain("<Assign.To>", result.Xaml);
        Assert.DoesNotContain("x:TypeArguments", result.Xaml);
    }

    [Fact]
    public void UntypedAssign_WarnsThatTheGenericFormIsPreferred() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "counter", ["value"] = "1" }
        };

        var result = XamlBuilder.RenderFragment(spec, ActivityCatalog.Fallback,
            new ProjectXamlSettings { ExpressionLanguage = ExpressionLanguage.CSharp });

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains(result.Warnings, w => w.Contains("Assign<T>", StringComparison.Ordinal));
    }

    [Fact]
    public void UntypedAssign_InCSharpProjectStillUsesTheTypedArgumentElements() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "counter", ["value"] = "counter + 1" }
        };

        var result = XamlBuilder.RenderFragment(spec, ActivityCatalog.Fallback,
            new ProjectXamlSettings { ExpressionLanguage = ExpressionLanguage.CSharp });

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<OutArgument x:TypeArguments=\"x:Object\">", result.Xaml);
        Assert.Contains("<CSharpReference x:TypeArguments=\"x:Object\">counter</CSharpReference>", result.Xaml);
    }

    [Fact]
    public void BracketsOnTypeArgument_AreRejected() {
        var spec = new ActivitySpec {
            Name = "Assign",
            Properties = new() { ["to"] = "[counter]", ["value"] = "[1]", ["typeArgument"] = "[Int32]" }
        };

        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public void UnknownProperty_NoLongerEmitsABareTypeArgumentsAttribute() {
        // The old passthrough wrote typeArguments="…" unprefixed, which is not a
        // XAML language member and looks like a XAML error.
        var spec = new ActivitySpec {
            Name = "LogMessage",
            Properties = new() { ["message"] = "[msg]", ["typeArguments"] = "x:Int32" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("x:TypeArguments=\"x:Int32\"", result.Xaml);
        Assert.DoesNotContain("typeArguments=", result.Xaml);
        Assert.Contains(result.Warnings, w => w.Contains("XAML language member", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownProperty_PassesThroughWithAWarning() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Properties = new() { ["ContinueOnError"] = "True" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("ContinueOnError=\"True\"", result.Xaml);
        Assert.Contains(result.Warnings, w => w.Contains("ContinueOnError", StringComparison.Ordinal)
            && w.Contains("not in the schema", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownProperty_DoesNotWarn() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Properties = new() { ["displayName"] = "Steps" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void UndeclaredPrefix_IsPassedThroughWithAWarning() {
        var spec = new ActivitySpec {
            Name = "Sequence",
            Properties = new() { ["uix:Something"] = "1" }
        };

        var result = XamlBuilder.RenderFragment(spec);

        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains(result.Warnings, w => w.Contains("uix", StringComparison.Ordinal)
            && w.Contains("does not declare", StringComparison.Ordinal));
    }
}
