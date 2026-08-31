using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ValidateProjectToolTests {
    [Fact]
    public async Task ValidateProject_WhenPathNotAllowed_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = false };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/not/allowed");

        Assert.Equal("error", result.Status);
        Assert.Equal("Path not allowed.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenProjectJsonMissing_ReturnsError() {
        var fs = new FakeFilesystemProvider { Allowed = true, ProjectJson = null };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/empty");

        Assert.Equal("error", result.Status);
        Assert.Equal("project.json not found.", result.Summary);
    }

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_ReturnsSuccess() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("success", result.Status);
        Assert.Equal("Validation completed.", result.Summary);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateProject_WhenCliFails_PropagatesErrorsAndWarnings() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = false,
                Summary = "Validation failed.",
                Errors = ["[validate] boom"],
                Warnings = ["[build] heads up"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Contains("[validate] boom", result.Errors);
        Assert.Contains("[build] heads up", result.Warnings);
    }

    [Fact]
    public async Task ValidateProject_WhenCliThrows_ReturnsStructuredErrorInsteadOfThrowing() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider { ValidateException = new InvalidOperationException("boom") };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");

        Assert.Equal("error", result.Status);
        Assert.Equal("Project validation failed.", result.Summary);
        Assert.Contains("boom", result.Errors[0]);
    }

    private static JsonElement SerializeData(object? data) =>
        JsonSerializer.SerializeToElement(data);

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_DataHasPerStepShapeAndNoRecommendations() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = true,
                Summary = "Validation completed.",
                Validate = new CliStepResult { Executed = true, Success = true },
                Build = new CliStepResult { Executed = true, Success = true, Warnings = ["[build] heads up"] }
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.True(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("executed").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("build").GetProperty("executed").GetBoolean());
        // pack was not executed -> distinguishable via executed:false, success:false.
        Assert.False(data.GetProperty("pack").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("pack").GetProperty("success").GetBoolean());
        Assert.Equal(0, data.GetProperty("recommendations").GetArrayLength());
    }

    [Fact]
    public async Task ValidateProject_WhenStepFails_DataMarksSkippedStepsAndRecommendsReview() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = false,
                Summary = "Validation failed.",
                Validate = new CliStepResult { Executed = true, Success = false, Errors = ["[validate] boom"] },
                Errors = ["[validate] boom"]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("validate").GetProperty("executed").GetBoolean());
        Assert.False(data.GetProperty("validate").GetProperty("success").GetBoolean());
        Assert.False(data.GetProperty("build").GetProperty("executed").GetBoolean());

        var recommendations = data.GetProperty("recommendations");
        Assert.Single(recommendations.EnumerateArray());
        Assert.Contains("validate", recommendations[0].GetString());
    }

    [Fact]
    public async Task ValidateProject_DefaultFlags_ValidateAndBuildOnly() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider();
        var tool = new ValidateProjectTool(cli, fs);

        await tool.ValidateProject("/projects/testProcess");

        Assert.Equal((true, true, false), cli.LastValidateFlags);
    }

    [Fact]
    public async Task ValidateProject_WhenCliSucceeds_DataHasEmptyDiagnostics() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.Equal(0, data.GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public async Task ValidateProject_WhenBoundaryViolation_ReturnsStructuredErrorEvenIfCliSucceeds() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var builder = new FakeProjectModelBuilder {
            Model = new UiPathProjectModel {
                ProjectName = "p",
                MainWorkflow = "Main.xaml",
                Workflows = [
                    new WorkflowModel {
                        FileName = "Main.xaml",
                        InvokeWorkflows = [
                            new InvokeWorkflowModel {
                                SourceWorkflow = "Main.xaml",
                                TargetWorkflow = "InvoiceFlow.cs",
                                ArgumentMappings = [
                                    new ArgumentMappingModel {
                                        Direction = "In",
                                        TargetArgument = "in_Customer",
                                        Type = "CustomerRecord"
                                    }
                                ]
                            }
                        ]
                    }
                ],
                CodedWorkflows = [
                    new CodedWorkflowModel {
                        FileName = "InvoiceFlow.cs",
                        ClassName = "InvoiceFlow",
                        Kind = CodedFileKind.Workflow,
                        IsCodedWorkflow = true
                    },
                    new CodedWorkflowModel {
                        FileName = "CustomerRecord.cs",
                        ClassName = "CustomerRecord",
                        Kind = CodedFileKind.Source
                    }
                ]
            }
        };
        var tool = new ValidateProjectTool(cli, fs, builder);

        var result = await tool.ValidateProject("/projects/testProcess", validate: true, build: false, pack: false);
        var data = SerializeData(result.Data);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == ToolErrorCodes.XamlCodedBoundary);
        Assert.False(data.GetProperty("success").GetBoolean());
        Assert.True(data.GetProperty("boundary").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ValidateProject_WhenCliSucceedsAndModelHasNoBoundaryIssues_StaysSuccess() {
        var fs = new FakeFilesystemProvider { Allowed = true };
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult { Success = true, Summary = "Validation completed." }
        };
        var builder = new FakeProjectModelBuilder {
            Model = new UiPathProjectModel {
                ProjectName = "p",
                MainWorkflow = "Main.xaml",
                Workflows = [new WorkflowModel { FileName = "Main.xaml" }]
            }
        };
        var tool = new ValidateProjectTool(cli, fs, builder);

        var result = await tool.ValidateProject("/projects/testProcess", validate: true, build: false, pack: false);

        Assert.Equal("success", result.Status);
        Assert.Empty(result.ErrorDetails);
    }

    [Fact]
    public async Task ValidateProject_MapsCliDiagnosticsOntoActivityIdAndSpecFix() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities"
                      xmlns:sap2010="http://schemas.microsoft.com/netfx/2010/xaml/activities/presentation">
              <Sequence sap2010:WorkflowViewState.IdRef="Sequence_1">
                <ui:LogMessage DisplayName="Log start" Message="[foo]" sap2010:WorkflowViewState.IdRef="LogMessage_1" />
              </Sequence>
            </Activity>
            """;
        var fs = new FakeFilesystemProvider { Allowed = true };
        fs.FileContents["/projects/testProcess/Main.xaml"] = xaml;
        var cli = new FakeUiPathCliProvider {
            Result = new UiPathCliResult {
                Success = false,
                Summary = "Validation failed.",
                Errors = ["[validate] Main.xaml(8): BC30451: 'foo' is not declared."],
                Validate = new CliStepResult { Executed = true, Success = false },
                Diagnostics = [
                    new CliDiagnostic {
                        Message = "'foo' is not declared.",
                        FilePath = "Main.xaml",
                        Line = 8,
                        IdRef = "LogMessage_1",
                        Property = "Message",
                        Code = "BC30451"
                    }
                ]
            }
        };
        var tool = new ValidateProjectTool(cli, fs);

        var result = await tool.ValidateProject("/projects/testProcess");
        var data = SerializeData(result.Data);

        Assert.Equal("error", result.Status);
        var diagnostic = Assert.Single(data.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("sequence.1/logmessage.1", diagnostic.GetProperty("activityId").GetString());
        Assert.Equal("Message", diagnostic.GetProperty("property").GetString());
        Assert.Equal("'foo' is not declared.", diagnostic.GetProperty("message").GetString());
        var specFix = diagnostic.GetProperty("specFix");
        Assert.Equal("Main.xaml", specFix.GetProperty("workflowFile").GetString());
        Assert.Equal("[foo]", specFix.GetProperty("properties").GetProperty("Message").GetString());
        Assert.False(string.IsNullOrWhiteSpace(specFix.GetProperty("hint").GetString()));
        Assert.Contains(
            data.GetProperty("recommendations").EnumerateArray().Select(e => e.GetString()),
            r => r is not null && r.Contains("diagnostics[].activityId", StringComparison.Ordinal));
    }
}
