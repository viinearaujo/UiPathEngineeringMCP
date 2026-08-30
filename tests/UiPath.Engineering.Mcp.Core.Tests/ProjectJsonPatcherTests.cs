using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Docs;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectJsonPatcherTests {
    private const string Sample = """
        {
          "name": "testProcess",
          "expressionLanguage": "CSharp",
          "targetFramework": "Windows",
          "schemaVersion": "4.0",
          "main": "Main.xaml",
          "entryPoints": [
            { "filePath": "Main.xaml", "uniqueId": "abc", "input": [], "output": [] }
          ],
          "dependencies": {
            "UiPath.System.Activities": "[24.10.4]"
          },
          "designOptions": {
            "outputType": "Process",
            "fileInfoCollection": []
          },
          "runtimeOptions": {
            "isAttended": false
          }
        }
        """;

    [Fact]
    public void AddAndRemoveEntryPoint() {
        var added = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.AddEntryPoint, filePath: "Worker.cs");
        Assert.True(added.Success);
        Assert.Contains("Worker.cs", added.UpdatedJson);

        var removed = ProjectJsonPatcher.Apply(added.UpdatedJson!, ProjectJsonPatcher.RemoveEntryPoint, filePath: "Worker.cs");
        Assert.True(removed.Success);
        Assert.DoesNotContain("Worker.cs", removed.UpdatedJson);
    }

    [Fact]
    public void UpsertAndRemoveDependency() {
        var upserted = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.UpsertDependency, packageId: "UiPath.Excel.Activities", version: "[2.24.0]");
        Assert.True(upserted.Success);
        Assert.Contains("UiPath.Excel.Activities", upserted.UpdatedJson);

        var removed = ProjectJsonPatcher.Apply(upserted.UpdatedJson!, ProjectJsonPatcher.RemoveDependency, packageId: "UiPath.Excel.Activities");
        Assert.True(removed.Success);
        Assert.DoesNotContain("UiPath.Excel.Activities", removed.UpdatedJson);
    }

    [Fact]
    public void UpsertAndRemoveFileInfo() {
        var upserted = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.UpsertFileInfo, filePath: "Tests/TestMain.xaml");
        Assert.True(upserted.Success);
        Assert.Contains("TestMain.xaml", upserted.UpdatedJson);

        var removed = ProjectJsonPatcher.Apply(upserted.UpdatedJson!, ProjectJsonPatcher.RemoveFileInfo, filePath: "Tests/TestMain.xaml");
        Assert.True(removed.Success);
        Assert.DoesNotContain("TestMain.xaml", removed.UpdatedJson);
    }

    [Fact]
    public void SetExceptionHandler_SetsAndClears() {
        var set = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.SetExceptionHandler, filePath: "GlobalHandler.xaml");
        Assert.True(set.Success);
        Assert.Contains("GlobalHandler.xaml", set.UpdatedJson);

        var cleared = ProjectJsonPatcher.Apply(set.UpdatedJson!, ProjectJsonPatcher.SetExceptionHandler, filePath: "");
        Assert.True(cleared.Success);
        Assert.DoesNotContain("exceptionHandlerWorkflow", cleared.UpdatedJson);
    }

    [Fact]
    public void SetRuntimeOption_WritesJsonValue() {
        var patched = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.SetRuntimeOption, key: "supportsPersistence", value: "true");
        Assert.True(patched.Success);
        Assert.Contains("\"supportsPersistence\": true", patched.UpdatedJson);
    }

    [Theory]
    [InlineData("expressionLanguage")]
    [InlineData("targetFramework")]
    [InlineData("schemaVersion")]
    public void ImmutableKeys_AreRefused(string key) {
        var patched = ProjectJsonPatcher.Apply(Sample, ProjectJsonPatcher.SetRuntimeOption, key: key, value: "\"nope\"");
        Assert.False(patched.Success);
        Assert.Equal(ToolErrorCodes.InvalidArgument, patched.ErrorCode);
        Assert.Contains(key, patched.Error);
    }

    [Fact]
    public void UnknownOperation_Fails() {
        var patched = ProjectJsonPatcher.Apply(Sample, "overwrite_all");
        Assert.False(patched.Success);
    }
}
