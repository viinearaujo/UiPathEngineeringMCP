using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

/// <summary>
/// <see cref="TypeToken"/> is shared by <see cref="XamlBuilder"/> and
/// <see cref="WorkflowSurfaceEditor"/> so the same user input renders the same
/// token in both. XamlBuilder declares the xmlns alias a token needs; the
/// surface editor does not — it is a whitespace-preserving surgical editor and
/// has no root-declaration pass. These tests pin that asymmetry so it is visible
/// rather than surprising.
/// </summary>
public class SharedTypeTokenTests {
    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Sequence DisplayName="Main">
            <WriteLine DisplayName="Say" Text="hi" />
          </Sequence>
        </Activity>
        """;

    [Fact]
    public void BuilderAndSurfaceEditor_RenderTheSameTokenForTheSameInput() {
        var built = XamlBuilder.RenderWorkflowFile(new ActivitySpec {
            Name = "Sequence",
            Variables = [new VariableSpec { Name = "table", Type = "DataTable" }]
        }, "Main");

        var edited = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "table", type: "DataTable");

        Assert.True(built.Success);
        Assert.True(edited.Success, edited.Error);
        Assert.Contains("<Variable x:TypeArguments=\"sd:DataTable\" Name=\"table\"", built.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"sd:DataTable\" Name=\"table\"", edited.UpdatedContent);
    }

    [Fact]
    public void Builder_DeclaresTheAliasTheTokenNeeds() {
        var result = XamlBuilder.RenderWorkflowFile(new ActivitySpec {
            Name = "Sequence",
            Variables = [new VariableSpec { Name = "table", Type = "DataTable" },
                new VariableSpec { Name = "when", Type = "DateTime" }]
        }, "Main");

        Assert.True(result.Success);
        Assert.Contains("xmlns:sd=\"clr-namespace:System.Data;assembly=System.Data\"", result.Xaml);
        Assert.Contains("xmlns:s=\"clr-namespace:System;assembly=System.Private.CoreLib\"", result.Xaml);
    }

    /// <summary>
    /// The surface editor emits the same corrected token but cannot add the xmlns
    /// declaration, so the caller must declare <c>sd</c>/<c>s</c> on the root
    /// Activity. Before TypeToken covered these types the token was a bare
    /// <c>DataTable</c>, which did not resolve either — the editor is not
    /// regressed, but it is still incomplete for non-<c>x:</c> primitives.
    /// </summary>
    [Fact]
    public void SurfaceEditor_DoesNotDeclareTheAliasItEmits() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "table", type: "DataTable");

        Assert.True(result.Success, result.Error);
        Assert.Contains("sd:DataTable", result.UpdatedContent);
        Assert.DoesNotContain("xmlns:sd=", result.UpdatedContent);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void SurfaceEditor_XamlPrimitive_NeedsNoExtraDeclaration() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "v", type: "String");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Variable x:TypeArguments=\"x:String\" Name=\"v\"", result.UpdatedContent);
    }

    /// <summary>
    /// A dotted full CLR name passes through verbatim. That is unchanged behavior,
    /// but common-pitfalls.md documents that <c>x:TypeArguments</c> never resolves
    /// dotted names — each subnamespace needs its own xmlns alias. Pinning it here
    /// so the pass-through is visible rather than assumed correct.
    /// </summary>
    [Fact]
    public void SurfaceEditor_DottedFullTypeName_PassesThroughVerbatim() {
        var result = WorkflowSurfaceEditor.Edit(Workflow, "add", "variable", "table",
            type: "System.Data.DataTable");

        Assert.True(result.Success, result.Error);
        Assert.Contains("<Variable x:TypeArguments=\"System.Data.DataTable\" Name=\"table\"",
            result.UpdatedContent);
    }
}
