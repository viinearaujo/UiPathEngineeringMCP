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
}
