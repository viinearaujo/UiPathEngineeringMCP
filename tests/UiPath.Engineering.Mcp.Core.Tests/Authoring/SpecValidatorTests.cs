using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class SpecValidatorTests
{
    [Fact]
    public void Validate_NullSpec_EmptySpec()
    {
        var e = Assert.Single(SpecValidator.Validate(null!));
        Assert.Equal(ToolErrorCodes.SpecEmptySpec, e.ErrorCode);
        Assert.Equal("validate_activity_spec", e.SuggestedTool);
    }

    [Fact]
    public void Validate_BlankName_EmptySpec()
    {
        var e = Assert.Single(SpecValidator.Validate(new ActivitySpec { Name = "  " }));
        Assert.Equal(ToolErrorCodes.SpecEmptySpec, e.ErrorCode);
        Assert.Equal("validate_activity_spec", e.SuggestedTool);
    }

    [Fact]
    public void Validate_UnknownActivity_SuggestsClosest()
    {
        var errors = SpecValidator.Validate(new ActivitySpec { Name = "FoeEach" });
        var e = Assert.Single(errors);
        Assert.Equal(ToolErrorCodes.SpecUnknownActivity, e.ErrorCode);
        Assert.Contains("ForEach", e.FixHint);
    }

    [Fact]
    public void Validate_MissingRequiredProperty_MissingRequired()
    {
        var spec = new ActivitySpec { Name = "Assign", Properties = new() { ["to"] = "[a]" } };
        var e = Assert.Single(SpecValidator.Validate(spec));
        Assert.Equal(ToolErrorCodes.SpecMissingRequiredProperty, e.ErrorCode);
        Assert.Contains("value", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_ChildrenOnLeaf_InvalidNesting()
    {
        var spec = new ActivitySpec { Name = "LogMessage",
            Properties = new() { ["message"] = "[x]" },
            Children = [new ActivitySpec { Name = "Rethrow" }] };
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
    }

    [Fact]
    public void Validate_VariablesOnNonRoot_InvalidNesting()
    {
        var spec = new ActivitySpec { Name = "Sequence", Children =
            [new ActivitySpec { Name = "Rethrow", Variables = [new VariableSpec { Name = "v", Type = "Int32" }] }] };
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
    }

    [Fact]
    public void Validate_CatchesOnNonTryCatch_InvalidNesting()
    {
        var spec = new ActivitySpec { Name = "Sequence", Children =
            [new ActivitySpec { Name = "If",
                Properties = new() { ["condition"] = "[flag]" },
                Catches = [new CatchSpec()] }] };
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecInvalidNesting);
    }

    [Fact]
    public void Validate_ExpressionGivenLiteral_Mismatch()
    {
        var spec = new ActivitySpec { Name = "Assign",
            Properties = new() { ["to"] = "counter", ["value"] = "[counter + 1]" } }; // "to" missing brackets
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public void Validate_LiteralGivenExpressionForm_Mismatch()
    {
        var spec = new ActivitySpec { Name = "LogMessage",
            Properties = new() { ["message"] = "[x]", ["level"] = "[Info]" } }; // Level is Literal, must not be bracket-wrapped
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public void Validate_TypeArgumentWithBrackets_Mismatch()
    {
        var spec = new ActivitySpec { Name = "ForEach",
            Properties = new() { ["values"] = "[items]", ["typeArgument"] = "[DataRow]" } };
        Assert.Contains(SpecValidator.Validate(spec), e => e.ErrorCode == ToolErrorCodes.SpecValueFormMismatch);
    }

    [Fact]
    public void Validate_MultipleViolations_AllCollected()
    {
        var spec = new ActivitySpec { Name = "Sequence", Children =
            [ new ActivitySpec { Name = "Bogus" },
              new ActivitySpec { Name = "Assign", Properties = new() { ["to"] = "[a]" } } ] }; // missing required "value"
        var errors = SpecValidator.Validate(spec);
        Assert.Contains(errors, e => e.ErrorCode == ToolErrorCodes.SpecUnknownActivity);
        Assert.Contains(errors, e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty);
    }

    [Fact]
    public void Validate_NestedViolation_PathInMessage()
    {
        var spec = new ActivitySpec { Name = "Sequence", Children =
            [new ActivitySpec { Name = "Sequence", Children =
                [new ActivitySpec { Name = "Bogus" }] }] };
        var e = Assert.Single(SpecValidator.Validate(spec));
        Assert.Contains("children[0].children[0]", e.Message);
    }

    [Fact]
    public void Validate_DesignDocExample_NoErrors()
    {
        const string json = """
        { "name": "Sequence",
          "variables": [{ "name": "rowCount", "type": "Int32", "default": "0" }],
          "children": [
            { "name": "ForEach",
              "properties": { "values": "[in_TransactionData]", "typeArgument": "DataRow" },
              "children": [
                { "name": "TryCatch",
                  "children": [ { "name": "LogMessage", "properties": { "message": "\"Processing row\"", "level": "Info" } } ],
                  "catches": [ { "exception": "System.Exception", "children": [ { "name": "Rethrow" } ] } ] } ] } ] }
        """;
        var spec = JsonSerializer.Deserialize<ActivitySpec>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(SpecValidator.Validate(spec));
    }
}
