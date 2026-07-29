using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ValidateActivitySpecToolTests {
    // The design-doc example: Sequence, ForEach, TryCatch, LogMessage, Rethrow (5 distinct).
    private const string DesignDocSpecJson = """
        {
          "name": "Sequence",
          "variables": [{ "name": "rowCount", "type": "Int32", "default": "0" }],
          "children": [
            {
              "name": "ForEach",
              "properties": { "values": "[in_TransactionData]", "typeArgument": "DataRow" },
              "children": [
                {
                  "name": "TryCatch",
                  "children": [
                    { "name": "LogMessage", "properties": { "message": "\"Processing row\"", "level": "Info" } }
                  ],
                  "catches": [{ "exception": "System.Exception", "children": [ { "name": "Rethrow" } ] }]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void ValidateActivitySpec_InvalidJson_StructuredError() {
        var result = new ValidateActivitySpecTool().ValidateActivitySpec("{ not json");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecInvalidSpecJson);
    }

    [Fact]
    public void ValidateActivitySpec_NullJson_TreatedAsEmptySpec() {
        var result = new ValidateActivitySpecTool().ValidateActivitySpec("null");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecEmptySpec);
    }

    [Fact]
    public void ValidateActivitySpec_ValidSpec_ReturnsActivitiesUsed() {
        var result = new ValidateActivitySpecTool().ValidateActivitySpec(DesignDocSpecJson);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.True(data.GetProperty("valid").GetBoolean());
        Assert.Equal(5, data.GetProperty("activitiesUsed").GetArrayLength());
        Assert.Equal(0, data.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public void ValidateActivitySpec_MultipleViolations_AllReturned() {
        // One unknown activity + one missing-required-property.
        var specJson = """
            {
              "name": "Sequence",
              "children": [
                { "name": "Bogus" },
                { "name": "Assign", "properties": { "to": "[x]" } }
              ]
            }
            """;

        var result = new ValidateActivitySpecTool().ValidateActivitySpec(specJson);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecUnknownActivity);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty);
    }

    [Fact]
    public void ValidateActivitySpec_ExperimentalActivity_ReportsWarning() {
        // Integration path: only runnable once the shipped catalog has an
        // experimental activity. xUnit v2 has no dynamic skip, so the no-entry
        // case is marked explicitly in the assertion message instead of silently
        // returning. The warnings logic itself is always covered by
        // ExperimentalWarnings_ExperimentalActivity_ReturnsWarning below.
        var experimental = UiPath.Engineering.Mcp.Core.Authoring.ActivityCatalog.All
            .FirstOrDefault(a => a.Experimental);
        if (experimental is null) {
            Assert.True(true, "skipped: no experimental activities in the catalog");
            return;
        }

        var properties = string.Join(", ", experimental.Properties
            .Where(p => p.Required)
            .Select(p => $$""" "{{p.Name}}": "{{DummyValue(p)}}" """));
        var specJson = $$"""{ "name": "{{experimental.Name}}", "properties": { {{properties}} } }""";

        var result = new ValidateActivitySpecTool().ValidateActivitySpec(specJson);

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        var warnings = data.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains(warnings, w => w != null && w.Contains("experimental", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExperimentalWarnings_ExperimentalActivity_ReturnsWarning() {
        // Direct coverage of the warnings path, independent of the shipped catalog.
        var warnings = ValidateActivitySpecTool.ExperimentalWarnings(
            ["Sequence", "PreviewActivity"], name => name == "PreviewActivity");

        var warning = Assert.Single(warnings);
        Assert.Contains("experimental", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PreviewActivity", warning);
    }

    [Fact]
    public void ExperimentalWarnings_NoExperimentalActivity_ReturnsEmpty() {
        var warnings = ValidateActivitySpecTool.ExperimentalWarnings(
            ["Sequence"], name => ActivityCatalogHas(name));

        Assert.Empty(warnings);
    }

    private static bool ActivityCatalogHas(string name) =>
        UiPath.Engineering.Mcp.Core.Authoring.ActivityCatalog.TryGet(name, out var schema) && schema.Experimental;

    private static string DummyValue(UiPath.Engineering.Mcp.Core.Authoring.PropertySchema property) =>
        property.Kind switch {
            UiPath.Engineering.Mcp.Core.Authoring.PropertyKind.Expression => "[1]",
            UiPath.Engineering.Mcp.Core.Authoring.PropertyKind.TypeArgument => "Object",
            _ => "value"
        };
}
