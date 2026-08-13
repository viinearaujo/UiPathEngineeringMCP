using System.Text.Json;
using UiPath.Engineering.Mcp.Core.Models;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class FindActivityToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static UiPathProjectModel SampleModel() {
        var sequence = new ActivityModel {
            Id = "sequence.1", DisplayName = "Main Sequence", Type = "Sequence", Depth = 0, Order = 0, Line = 5
        };
        var ifActivity = new ActivityModel {
            Id = "sequence.1/if.1", ParentId = "sequence.1", DisplayName = "If connected",
            Type = "If", Depth = 1, Order = 1, Line = 6
        };
        var log = new ActivityModel {
            Id = "sequence.1/if.1/logmessage.1", ParentId = "sequence.1/if.1", DisplayName = "Log start",
            Type = "LogMessage", Depth = 2, Order = 2, Line = 7
        };
        return new UiPathProjectModel {
            ProjectName = "testProcess",
            MainWorkflow = "Main.xaml",
            Workflows = [
                new WorkflowModel { FileName = "Main.xaml", Activities = [sequence, ifActivity, log] },
                new WorkflowModel {
                    FileName = "Child.xaml",
                    Activities = [new ActivityModel {
                        Id = "sequence.1", DisplayName = "Child seq", Type = "Sequence", Depth = 0, Order = 0, Line = 3
                    }]
                }
            ]
        };
    }

    private static FindActivityTool Tool(UiPathProjectModel model) =>
        new(new FakeFilesystemProvider(), new FakeProjectModelBuilder { Model = model });

    [Fact]
    public async Task Query_FiltersByDisplayNameCaseInsensitively() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, query: "log");

        Assert.Equal("success", result.Status);
        var match = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches")[0];
        Assert.Equal("sequence.1/if.1/logmessage.1", match.GetProperty("id").GetString());
        Assert.Equal("Main.xaml", match.GetProperty("workflowFile").GetString());
        Assert.Equal(7, match.GetProperty("line").GetInt32());
        Assert.Equal("sequence.1/if.1", match.GetProperty("parentId").GetString());
        var ancestors = match.GetProperty("ancestors");
        Assert.Equal(2, ancestors.GetArrayLength());
        Assert.Equal("sequence.1", ancestors[0].GetProperty("id").GetString());
        Assert.Equal("sequence.1/if.1", ancestors[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ActivityId_LooksUpExactActivity() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, activityId: "sequence.1/if.1");

        Assert.Equal("success", result.Status);
        var matches = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("If", matches[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task WorkflowFileAndType_NarrowTheSearch() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath,
            workflowFile: "Child.xaml", activityType: "Sequence");

        Assert.Equal("success", result.Status);
        var matches = JsonSerializer.SerializeToElement(result.Data).GetProperty("matches");
        Assert.Equal(1, matches.GetArrayLength());
        Assert.Equal("Child.xaml", matches[0].GetProperty("workflowFile").GetString());
    }

    [Fact]
    public async Task NoMatches_IsSuccessWithNote() {
        var result = await Tool(SampleModel()).FindActivity(ProjectPath, query: "does-not-exist");

        Assert.Equal("success", result.Status);
        var data = JsonSerializer.SerializeToElement(result.Data);
        Assert.Equal(0, data.GetProperty("matches").GetArrayLength());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("note").GetString()));
    }
}
