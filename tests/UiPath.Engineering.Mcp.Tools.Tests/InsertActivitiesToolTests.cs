namespace UiPath.Engineering.Mcp.Tools.Tests;

public class InsertActivitiesToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private const string Workflow = """
        <Activity x:Class="Main"
          xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
          xmlns:ui="http://schemas.uipath.com/workflow/activities">
          <Sequence DisplayName="Main" />
        </Activity>
        """;

    private const string AssignSpec = """
        {
          "name": "Sequence",
          "children": [
            { "name": "Assign", "properties": { "DisplayName": "Set total", "To": "[total]", "Value": "[42]" } }
          ]
        }
        """;

    private static (FakeFilesystemProvider Fs, InsertActivitiesTool Tool, string Target) CreateTool() {
        var fs = new FakeFilesystemProvider();
        var target = Path.Combine(Path.GetFullPath(ProjectPath), "Main.xaml");
        fs.FileContents[target] = Workflow;
        return (fs, new InsertActivitiesTool(fs), target);
    }

    [Fact]
    public void InsertActivities_HappyPath_WritesOriginalContentPlusNewActivity() {
        var (fs, tool, target) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec, displayName: "Main");

        Assert.Equal("success", result.Status);
        var written = fs.Writes[target];
        Assert.Contains("DisplayName=\"Main\"", written);
        Assert.Contains("<Assign", written);
        Assert.Contains("DisplayName=\"Set total\"", written);
    }

    [Fact]
    public void InsertActivities_WhenSpecInvalid_ReturnsErrorAndDoesNotWrite() {
        var (fs, tool, target) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.xaml",
            """{ "name": "NoSuchActivity", "properties": { "DisplayName": "X" } }""", displayName: "Main");

        Assert.Equal("error", result.Status);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public void InsertActivities_WhenSpecJsonMalformed_ReturnsErrorAndDoesNotWrite() {
        var (fs, tool, target) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", "{ not json", displayName: "Main");

        Assert.Equal("error", result.Status);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public void InsertActivities_WhenTargetNotFound_SurfacesEditorError() {
        var (fs, tool, target) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.xaml", AssignSpec, displayName: "Missing");

        Assert.Equal("error", result.Status);
        Assert.Contains("No activity found", result.Summary);
        Assert.False(fs.Writes.ContainsKey(target));
    }

    [Fact]
    public void InsertActivities_SequenceRootWithoutVariables_InsertsChildrenNotWrapper() {
        var (fs, tool, target) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.xaml",
            """
            {
              "name": "Sequence",
              "children": [
                { "name": "Assign", "properties": { "DisplayName": "A", "To": "[x]", "Value": "[1]" } },
                { "name": "Assign", "properties": { "DisplayName": "B", "To": "[y]", "Value": "[2]" } }
              ]
            }
            """, displayName: "Main");

        Assert.Equal("success", result.Status);
        var written = fs.Writes[target];
        // Both children are inserted directly; no extra wrapping <Sequence> around them.
        Assert.Contains("DisplayName=\"A\"", written);
        Assert.Contains("DisplayName=\"B\"", written);
        Assert.Equal(1, CountOccurrences(written, "<Sequence"));
    }

    [Fact]
    public void InsertActivities_RejectsNonXamlFile() {
        var (_, tool, _) = CreateTool();

        var result = tool.InsertActivities(ProjectPath, "Main.cs", AssignSpec, displayName: "Main");

        Assert.Equal("error", result.Status);
        Assert.Contains(".xaml", result.Summary);
    }

    private static int CountOccurrences(string text, string needle) {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0) {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
