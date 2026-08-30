using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ValidateDiagnosticMapperTests {
    private const string ProjectPath = "/projects/testProcess";
    private const string MainXamlPath = "/projects/testProcess/Main.xaml";

    private const string SampleXaml = """
        <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                  xmlns:ui="http://schemas.uipath.com/workflow/activities"
                  xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
          <Sequence DisplayName="Main Sequence" sap2010:WorkflowViewState.IdRef="Sequence_1">
            <ui:LogMessage DisplayName="Log start" Message="[foo]" sap2010:WorkflowViewState.IdRef="LogMessage_1" />
            <WriteLine DisplayName="Write done" Text="done" sap2010:WorkflowViewState.IdRef="WriteLine_1" />
          </Sequence>
        </Activity>
        """;

    private static FakeFilesystemProvider ProjectWithMain() {
        var fs = new FakeFilesystemProvider { ProjectJsonPath = $"{ProjectPath}/project.json" };
        fs.XamlFiles.Add(MainXamlPath);
        fs.FileContents[MainXamlPath] = SampleXaml;
        return fs;
    }

    [Fact]
    public void Map_ResolvesActivityIdFromIdRef() {
        var mapped = ValidateDiagnosticMapper.Map(ProjectPath, ProjectWithMain(), [
            new CliDiagnostic {
                Message = "'foo' is not declared.",
                FilePath = "Main.xaml",
                IdRef = "LogMessage_1",
                Property = "Message"
            }
        ]);

        var diagnostic = Assert.Single(mapped);
        Assert.Equal("sequence.1/logmessage.1", diagnostic.ActivityId);
        Assert.Equal("Message", diagnostic.Property);
        Assert.Equal("'foo' is not declared.", diagnostic.Message);
        Assert.NotNull(diagnostic.SpecFix);
        Assert.Equal("Main.xaml", diagnostic.SpecFix.WorkflowFile);
        Assert.Equal("[foo]", diagnostic.SpecFix.Properties!["Message"]);
        Assert.Contains("activityId", diagnostic.SpecFix.Hint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_CanonicalizesPropertyToCatalogName() {
        var mapped = ValidateDiagnosticMapper.Map(ProjectPath, ProjectWithMain(), [
            new CliDiagnostic {
                Message = "The property 'MessageText' does not exist.",
                FilePath = "Main.xaml",
                IdRef = "LogMessage_1",
                Property = "ui:LogMessage.Message"
            }
        ]);

        Assert.Equal("Message", Assert.Single(mapped).Property);
    }

    [Fact]
    public void Map_FallsBackToDisplayNameThenLine() {
        var mapped = ValidateDiagnosticMapper.Map(ProjectPath, ProjectWithMain(), [
            new CliDiagnostic {
                Message = "WriteLine text is empty.",
                FilePath = "Main.xaml",
                DisplayName = "Write done",
                Property = "Text"
            }
        ]);

        Assert.Equal("sequence.1/writeline.2", Assert.Single(mapped).ActivityId);
    }

    [Fact]
    public void Map_UnknownLocation_LeavesActivityIdNullButKeepsMessage() {
        var mapped = ValidateDiagnosticMapper.Map(ProjectPath, ProjectWithMain(), [
            new CliDiagnostic {
                Message = "Dependency UiPath.Excel.Activities is not used.",
                Code = "ST-USG-010"
            }
        ]);

        var diagnostic = Assert.Single(mapped);
        Assert.Null(diagnostic.ActivityId);
        Assert.Null(diagnostic.Property);
        Assert.Equal("Dependency UiPath.Excel.Activities is not used.", diagnostic.Message);
    }

    [Fact]
    public void Map_UsesRecommendationAsSpecFixHint() {
        var mapped = ValidateDiagnosticMapper.Map(ProjectPath, ProjectWithMain(), [
            new CliDiagnostic {
                Message = "Activity names should follow the naming convention.",
                FilePath = "Main.xaml",
                DisplayName = "Log start",
                Property = "DisplayName",
                Recommendation = "Rename the activity to include the type."
            }
        ]);

        Assert.Equal("Rename the activity to include the type.", Assert.Single(mapped).SpecFix!.Hint);
    }
}
