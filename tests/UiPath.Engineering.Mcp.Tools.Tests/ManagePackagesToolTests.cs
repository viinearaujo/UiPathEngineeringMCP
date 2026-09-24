using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class ManagePackagesToolTests {
    private static (ManagePackagesTool Sut, RecordingStructuredCli Cli) CreateSut(
        string stdOut = """{"Result":"Success","Data":{}}""", bool mutating = false) {
        var cli = new RecordingStructuredCli { StdOut = stdOut };
        var filesystem = CliToolFixtures.ProjectFilesystem();
        return (new ManagePackagesTool(cli, filesystem, CliToolFixtures.Policy(mutating)), cli);
    }

    [Fact]
    public async Task Versions_DefaultsToIncludePrerelease() {
        var (sut, cli) = CreateSut("""
            {"Result":"Success","Data":{"packageId":"UiPath.Excel.Activities","includePrerelease":true,"versions":["3.6.1","3.6.0-preview","3.5.3"]}}
            """);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "UiPath.Excel.Activities");

        Assert.Equal("success", result.Status);
        // Activity packages frequently ship -preview carrying the freshest surface, so the flag is on by default.
        Assert.Contains("--include-prerelease", cli.LastTokens!);
        var data = CliToolFixtures.Data<VersionsPayload>(result)!;
        Assert.Equal("3.6.1", data.Latest);
        Assert.Equal("3.6.1", data.LatestStable);
        Assert.Equal(3, data.Versions.Count);
    }

    [Fact]
    public async Task Versions_CanOptOutOfPrerelease() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"packageId":"P","includePrerelease":false,"versions":["1.0.0"]}}""");

        await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "P", includePrerelease: false);

        Assert.DoesNotContain("--include-prerelease", cli.LastTokens!);
    }

    [Fact]
    public async Task Versions_LatestStableSkipsPrereleaseOnly() {
        var (sut, _) = CreateSut("""
            {"Result":"Success","Data":{"packageId":"P","includePrerelease":true,"versions":["4.0.0-preview","3.9.1"]}}
            """);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "P");
        var data = CliToolFixtures.Data<VersionsPayload>(result)!;

        Assert.Equal("4.0.0-preview", data.Latest);
        Assert.Equal("3.9.1", data.LatestStable);
    }

    [Fact]
    public async Task Versions_WithoutPackageId_IsRefused() {
        var (sut, cli) = CreateSut();

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Versions_NoVersionsFound_Warns() {
        var (sut, _) = CreateSut("""{"Result":"Success","Data":{"packageId":"UiPath.Fake","versions":[]}}""");

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "UiPath.Fake");

        Assert.Equal("success", result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("No versions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Versions_IsReadOnly_AndNeedsNoEnableMutatingCommands() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"packageId":"P","versions":["1.0.0"]}}""");

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "P");

        Assert.Equal("success", result.Status);
        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task Install_OmittingVersion_ResolvesLatestCompatible() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"failedPackages":[]}}""", mutating: true);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "install", packageId: "UiPath.Excel.Activities");

        Assert.Equal("success", result.Status);
        // Omitting the version is the preferred path per the CLI reference.
        Assert.Contains("id=UiPath.Excel.Activities", cli.LastTokens!);
        Assert.DoesNotContain(cli.LastTokens!, t => t.Contains("version=", StringComparison.Ordinal));
        Assert.True(CliToolFixtures.Data<InstallPayload>(result)!.ResolvedLatest);
    }

    [Fact]
    public async Task Install_PinnedVersion_IsCommaJoined() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"failedPackages":[]}}""", mutating: true);

        await sut.ManagePackages(CliToolFixtures.ProjectPath, "install", packageId: "UiPath.System.Activities", version: "23.10.1");

        Assert.Contains("id=UiPath.System.Activities,version=23.10.1", cli.LastTokens!);
    }

    [Fact]
    public async Task Install_WithoutEnableMutatingCommands_IsRefused() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{}}""", mutating: false);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "install", packageId: "UiPath.Excel.Activities");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "MUTATING_COMMAND_DISABLED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Install_FailedPackage_ReturnsErrorWithRecoveryHint() {
        var (sut, _) = CreateSut("""
            {"Result":"Success","Data":{"failedPackages":[{"id":"UiPath.Fake","version":"1.0.0","message":"Package not found on the feed."}]}}
            """, mutating: true);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "install", packageId: "UiPath.Fake", version: "1.0.0");

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Contains("UiPath.Fake@1.0.0", error.Message);
        Assert.Contains("find_activity", error.FixHint);
        Assert.Equal("find_activity", error.SuggestedTool);
    }

    [Fact]
    public async Task Install_FailureEnvelope_ReturnsStructuredError() {
        var (sut, _) = CreateSut("""{"Result":"Failure","Message":"The NuGet feed is unreachable.","Instructions":"Check the feed configuration."}""", mutating: true);

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "install", packageId: "P");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "OPERATION_FAILED");
    }

    [Fact]
    public async Task Install_NugetSourcesConfigOutsideAllowedRoots_IsRefused() {
        var cli = new RecordingStructuredCli();
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem();
        var sut = new ManagePackagesTool(cli, filesystem, CliToolFixtures.Policy(mutating: true));

        var result = await sut.ManagePackages(
            CliToolFixtures.ProjectPath, "install", packageId: "P",
            nugetSourcesConfigPath: @"C:\elsewhere\sources.json");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PATH_NOT_ALLOWED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Install_NugetSourcesConfigInsideTheProject_IsAccepted() {
        var cli = new RecordingStructuredCli { StdOut = """{"Result":"Success","Data":{"failedPackages":[]}}""" };
        var sourcesPath = Path.Combine(CliToolFixtures.ProjectPath, "feeds.json");
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem().WithFile(sourcesPath);
        var sut = new ManagePackagesTool(cli, filesystem, CliToolFixtures.Policy(mutating: true));

        var result = await sut.ManagePackages(
            CliToolFixtures.ProjectPath, "install", packageId: "P", nugetSourcesConfigPath: sourcesPath);

        Assert.Equal("success", result.Status);
        Assert.Contains("--nuget-sources-config-path", cli.LastTokens!);
    }

    [Fact]
    public async Task Inspect_WithPackageName_ReturnsMarkdown() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"markdown":"# UiPath.Excel.Activities API\n\n## Types"}}""");

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "inspect", packageName: "UiPath.Excel.Activities");

        Assert.Equal("success", result.Status);
        Assert.Contains("--package-name", cli.LastTokens!);
        Assert.Contains("UiPath.Excel.Activities API", CliToolFixtures.Data<InspectPayload>(result)!.Documentation);
    }

    [Fact]
    public async Task Inspect_WithNupkgPath_SkipsTheFeed() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":"# API"}""");

        var result = await sut.ManagePackages(
            CliToolFixtures.ProjectPath, "inspect", nupkgPath: Path.Combine(CliToolFixtures.ProjectPath, "libs", "P.1.0.0.nupkg"));

        Assert.Equal("success", result.Status);
        Assert.Contains("--nupkg-path", cli.LastTokens!);
        Assert.DoesNotContain("--package-name", cli.LastTokens!);
    }

    [Fact]
    public async Task Inspect_WithoutPackageNameOrNupkgPath_IsRefused() {
        var (sut, cli) = CreateSut();

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "inspect");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task Inspect_IsReadOnly_AndNeedsNoEnableMutatingCommands() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":"# API"}""");

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "inspect", packageName: "P");

        Assert.Equal("success", result.Status);
        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task UnknownOperation_IsRefused() {
        var (sut, cli) = CreateSut();

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "add-dependency", packageId: "P");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task MissingProjectJson_IsRefused() {
        var cli = new RecordingStructuredCli();
        var sut = new ManagePackagesTool(cli, new FakeFilesystemProvider { ProjectJson = null }, CliToolFixtures.Policy());

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "P");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PROJECT_JSON_NOT_FOUND");
    }

    [Fact]
    public async Task UnparseablePayload_ReturnsCliUnparseableResponse() {
        var (sut, _) = CreateSut("not json");

        var result = await sut.ManagePackages(CliToolFixtures.ProjectPath, "versions", packageId: "P");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "CLI_UNPARSEABLE_RESPONSE");
    }

    private sealed record VersionsPayload(
        string Operation, string PackageId, bool IncludePrerelease,
        string? Latest, string? LatestStable, List<string> Versions, string Command, string Note);

    private sealed record InstallPayload(
        string Operation, List<string> Requested, bool ResolvedLatest, List<string> Failed, string Command);

    private sealed record InspectPayload(
        string Operation, string? PackageName, string? PackageVersion, string? NupkgPath, string Command, string Documentation);
}
