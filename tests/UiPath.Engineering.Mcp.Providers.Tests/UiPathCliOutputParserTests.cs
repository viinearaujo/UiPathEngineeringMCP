using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class UiPathCliOutputParserTests
{
    private const string CleanRestoreOutput = """
        Restoring packages for C:\projects\testProcess\project.json...
        Installed UiPath.System.Activities 24.10.3 from https://pkgs.dev.azure.com/uipath/nuget/v3/index.json
        Restore completed in 2.4s.
        """;

    private const string AnalyzeWithIssuesOutput = """
        Analyzing project C:\projects\testProcess\project.json...
        Error  ST-USG-010 : Dependency UiPath.Excel.Activities is not used.
        Warning ST-DBP-020 : Activity 'Write Line' should not be used in production.
        Analysis completed with 1 error(s) and 1 warning(s).
        """;

    [Fact]
    public void Parse_CleanOutput_ProducesNoEntries()
    {
        var (errors, warnings) = UiPathCliOutputParser.Parse("restore", CleanRestoreOutput, "");

        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Parse_AnalyzerErrorAndWarningLines_ExtractsStructuredEntries()
    {
        var (errors, warnings) = UiPathCliOutputParser.Parse("analyze", AnalyzeWithIssuesOutput, "");

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("ST-USG-010") && e.Contains("UiPath.Excel.Activities is not used"));
        Assert.Single(warnings);
        Assert.Contains(warnings, w => w.Contains("ST-DBP-020") && w.Contains("Write Line"));
    }

    [Fact]
    public void Parse_NuGetStyleErrorLine_ExtractsCodeAndMessage()
    {
        var stdOut = "error NU1101: Unable to find package UiPath.Fake.Package. No packages exist with this id.";

        var (errors, warnings) = UiPathCliOutputParser.Parse("restore", stdOut, "");

        Assert.Single(errors);
        Assert.Equal("[restore] NU1101: Unable to find package UiPath.Fake.Package. No packages exist with this id.", errors[0]);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Parse_LowercaseWarningPrefix_GoesToWarnings()
    {
        var stdOut = "warning: The project uses a legacy dependency format.";

        var (errors, warnings) = UiPathCliOutputParser.Parse("restore", stdOut, "");

        Assert.Empty(errors);
        Assert.Single(warnings);
        Assert.Equal("[restore] The project uses a legacy dependency format.", warnings[0]);
    }

    [Fact]
    public void Parse_UnrecognizedLineMentioningError_IsPreservedVerbatim()
    {
        var stdOut = "The operation completed with 3 unexpected errors.";

        var (errors, warnings) = UiPathCliOutputParser.Parse("pack", stdOut, "");

        Assert.Single(errors);
        Assert.Equal("[pack] The operation completed with 3 unexpected errors.", errors[0]);
    }

    [Fact]
    public void Parse_StdErrLines_BecomeErrors()
    {
        var stdErr = "System.IO.IOException: The process cannot access the file.";

        var (errors, warnings) = UiPathCliOutputParser.Parse("analyze", "", stdErr);

        Assert.Single(errors);
        Assert.Equal("[analyze] System.IO.IOException: The process cannot access the file.", errors[0]);
        Assert.Empty(warnings);
    }

    [Fact]
    public void Parse_EmptyOutput_ReturnsEmptyLists()
    {
        var (errors, warnings) = UiPathCliOutputParser.Parse("restore", "", null);

        Assert.Empty(errors);
        Assert.Empty(warnings);
    }
}
