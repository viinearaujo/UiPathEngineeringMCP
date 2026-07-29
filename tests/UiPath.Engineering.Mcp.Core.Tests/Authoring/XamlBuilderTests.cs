using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class XamlBuilderTests
{
    [Fact]
    public void RenderFragment_Assign_RendersExpressionAttributes()
    {
        var spec = new ActivitySpec { Name = "Assign",
            Properties = new() { ["to"] = "[counter]", ["value"] = "[counter + 1]" } };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success);
        Assert.Contains("<Assign", result.Xaml);
        Assert.Contains("To=\"[counter]\"", result.Xaml);
        Assert.Contains("Value=\"[counter + 1]\"", result.Xaml);
    }

    [Fact]
    public void RenderFragment_ForEach_RendersActivityActionShape()
    {
        var spec = new ActivitySpec { Name = "ForEach",
            Properties = new() { ["values"] = "[rows]", ["typeArgument"] = "DataRow", ["itemName"] = "row" },
            Children = [new ActivitySpec { Name = "LogMessage", Properties = new() { ["message"] = "[row(0).ToString()]" } }] };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.Contains("<ForEach x:TypeArguments=\"DataRow\"", result.Xaml);
        Assert.Contains("<DelegateInArgument x:TypeArguments=\"DataRow\" Name=\"row\" />", result.Xaml);
        Assert.Contains("<ui:LogMessage", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_DesignDocExample_RoundTripsThroughParser()
    {
        // deserialize the design-doc example JSON (same literal as Task 2 test)
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
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("x:Class=\"TestWorkflow\"", result.Xaml);
        Assert.Contains("<TryCatch>", result.Xaml);
        Assert.Contains("<TryCatch.Catches>", result.Xaml);
        Assert.Contains("<Catch x:TypeArguments=\"System.Exception\">", result.Xaml);
        // round-trip is asserted inside RenderWorkflowFile itself; reaching Success proves it
    }

    [Fact]
    public void RenderWorkflowFile_VariableBareNonPrimitiveType_PassesThroughWithoutXPrefix()
    {
        var spec = new ActivitySpec { Name = "Sequence",
            Variables = [new VariableSpec { Name = "row", Type = "DataRow" },
                         new VariableSpec { Name = "n", Type = "Int32" }] };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Variable x:TypeArguments=\"DataRow\" Name=\"row\" />", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"n\" />", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_InvalidSpec_ShortCircuitsWithValidatorErrors()
    {
        var spec = new ActivitySpec { Name = "Assign",
            Properties = new() { ["to"] = "[x]" } }; // missing required "value"
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.False(result.Success);
        Assert.Null(result.Xaml);
        Assert.Contains(result.Errors, e => e.ErrorCode == ToolErrorCodes.SpecMissingRequiredProperty);
    }

    [Fact]
    public void RenderFragment_If_RendersThenBranchShape()
    {
        var spec = new ActivitySpec { Name = "If",
            Properties = new() { ["condition"] = "[counter > 0]" },
            Children = [new ActivitySpec { Name = "WriteLine", Properties = new() { ["text"] = "\"positive\"" } }] };
        var result = XamlBuilder.RenderFragment(spec);
        Assert.True(result.Success);
        Assert.Contains("<If Condition=\"[counter &gt; 0]\"", result.Xaml);
        Assert.Contains("<If.Then>", result.Xaml);
        Assert.Contains("<WriteLine", result.Xaml);
    }

    [Fact]
    public void RenderWorkflowFile_VariablesOnNonSequenceRoot_WrapsInOuterSequence()
    {
        var spec = new ActivitySpec { Name = "LogMessage",
            Properties = new() { ["message"] = "\"hi\"" },
            Variables = [new VariableSpec { Name = "n", Type = "Int32" }] };
        var result = XamlBuilder.RenderWorkflowFile(spec, "TestWorkflow");
        Assert.True(result.Success, string.Join(";", result.Errors.Select(e => e.Message)));
        Assert.Contains("<Sequence.Variables>", result.Xaml);
        Assert.Contains("<Variable x:TypeArguments=\"x:Int32\" Name=\"n\" />", result.Xaml);
    }
}
