using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ActivitySpecTests
{
    [Fact]
    public void Deserialize_DesignDocExample_MapsAllNodes()
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
        Assert.Equal("Sequence", spec.Name);
        Assert.Equal("Int32", spec.Variables![0].Type);
        var forEach = spec.Children![0];
        Assert.Equal("DataRow", forEach.Properties!["typeArgument"]);
        Assert.Equal("Rethrow", forEach.Children![0].Catches![0].Children![0].Name);
    }

    [Fact]
    public void Deserialize_IfElseSwitchAndInvokeArguments()
    {
        const string json = """
        {
          "name": "Sequence",
          "children": [
            {
              "name": "If",
              "properties": { "condition": "[ok]" },
              "children": [{ "name": "Rethrow" }],
              "else": [{ "name": "WriteLine", "properties": { "text": "\"no\"" } }]
            },
            {
              "name": "Switch",
              "properties": { "expression": "[status]", "typeArgument": "Int32" },
              "cases": [{ "key": "1", "children": [{ "name": "Rethrow" }] }],
              "default": [{ "name": "Rethrow" }]
            },
            {
              "name": "InvokeWorkflowFile",
              "properties": { "workflowFileName": "Child.xaml" },
              "arguments": [{ "name": "in_Path", "direction": "In", "type": "String", "value": "[p]" }]
            }
          ]
        }
        """;
        var spec = JsonSerializer.Deserialize<ActivitySpec>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("Rethrow", spec.Children![0].Children![0].Name);
        Assert.Equal("WriteLine", spec.Children[0].Else![0].Name);
        Assert.Equal("1", spec.Children[1].Cases![0].Key);
        Assert.Equal("in_Path", spec.Children[2].Arguments![0].Name);
    }
}
