using System.Text.Json;
using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class BuildWorkflowToolTests {
    private const string ProjectPath = "/projects/testProcess";
    private static readonly string Target =
        Path.Combine(Path.GetFullPath(ProjectPath), "Workflows", "Process.xaml");

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

    private static (BuildWorkflowTool Tool, FakeFilesystemProvider Fs) CreateTool() {
        var fs = new FakeFilesystemProvider();
        return (new BuildWorkflowTool(fs), fs);
    }

    [Fact]
    public void BuildWorkflow_PathNotAllowed_Error() {
        var (tool, fs) = CreateTool();
        fs.Allowed = false;

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", DesignDocSpecJson);

        Assert.Equal("error", result.Status);
    }

    [Fact]
    public void BuildWorkflow_ExistingFileWithoutOverwrite_ErrorAndNoWrite() {
        var (tool, fs) = CreateTool();
        fs.ExistingFiles.Add(Target);

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", DesignDocSpecJson);

        Assert.Equal("error", result.Status);
        Assert.Contains("overwrite", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.False(fs.Writes.ContainsKey(Target));
    }

    [Fact]
    public void BuildWorkflow_InvalidSpec_ErrorDetailsAndNoWrite() {
        var (tool, fs) = CreateTool();
        var specJson = """
            {
              "name": "Sequence",
              "children": [ { "name": "Bogus" } ]
            }
            """;

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", specJson);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecUnknownActivity);
        Assert.False(fs.Writes.ContainsKey(Target));
    }

    [Fact]
    public void BuildWorkflow_InvalidJson_ErrorDetailsAndNoWrite() {
        var (tool, fs) = CreateTool();

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", "{ not json");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.SpecInvalidSpecJson);
        Assert.False(fs.Writes.ContainsKey(Target));
    }

    [Fact]
    public void BuildWorkflow_ValidSpec_WritesFileAndSummary() {
        var (tool, fs) = CreateTool();

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.xaml", DesignDocSpecJson);

        Assert.Equal("success", result.Status);
        var xaml = fs.Writes[Target];
        Assert.Contains("x:Class=\"Workflows_Process\"", xaml);
        Assert.Contains("<ui:LogMessage", xaml);

        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(Target, data.GetProperty("filePath").GetString());
        Assert.Equal("Workflows_Process", data.GetProperty("xamlClass").GetString());
        Assert.Equal(5, data.GetProperty("activitiesUsed").GetArrayLength());
    }

    [Fact]
    public void BuildWorkflow_NonXamlExtension_Error() {
        var (tool, fs) = CreateTool();

        var result = tool.BuildWorkflow(ProjectPath, "Workflows/Process.json", DesignDocSpecJson);

        Assert.Equal("error", result.Status);
        Assert.Contains(".xaml", result.Summary);
    }
}
