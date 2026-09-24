using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Tools;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetObjectRepositoryToolTests {
    // Live shape from `uip rpa get-object-repository --output json`.
    private const string ProjectPayload = """
        {
          "Result": "Success",
          "Code": "ToolResult",
          "Data": [
            {
              "name": "CourtView",
              "description": "",
              "type": "App",
              "reference": "ref-app",
              "children": [
                {
                  "name": "General Application",
                  "type": "Screen",
                  "reference": "ref-screen",
                  "children": [
                    { "name": "Close", "taxonomyType": "Button", "type": "Element", "reference": "ref-close", "children": [] },
                    {
                      "name": "File Menu Option",
                      "taxonomyType": "Tags",
                      "type": "Element",
                      "reference": "ref-menu",
                      "children": [
                        { "name": "Close All Modules Menu Option", "taxonomyType": "Tags", "type": "Element", "reference": "ref-nested", "children": [] }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static (GetObjectRepositoryTool Sut, RecordingStructuredCli Cli) CreateSut(string stdOut = ProjectPayload) {
        var cli = new RecordingStructuredCli { StdOut = stdOut };
        var filesystem = CliToolFixtures.ProjectFilesystem();
        return (new GetObjectRepositoryTool(cli, filesystem, CliToolFixtures.Policy()), cli);
    }

    [Fact]
    public async Task ProjectSource_RunsTheProjectReadVerb() {
        var (sut, cli) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Equal(
            ["rpa", "get-object-repository", "--project-dir", CliToolFixtures.ProjectPath, "--output", "json"],
            cli.LastTokens);
    }

    [Fact]
    public async Task ReturnsAppsScreensAndElements() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);
        var data = CliToolFixtures.Data<OrPayload>(result)!;

        Assert.Equal("project", data.Source);
        Assert.Equal(1, data.Apps);
        Assert.Equal(1, data.Screens);
        Assert.Equal(3, data.Elements);
        Assert.Equal(5, data.Total);
    }

    [Fact]
    public async Task TreeCarriesNameTypeTaxonomyAndReference() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);
        var app = CliToolFixtures.Data<OrPayload>(result)!.Tree[0];

        Assert.Equal("CourtView", app.Name);
        Assert.Equal("App", app.Type);
        Assert.Equal("ref-app", app.Reference);
        Assert.Equal("CourtView", app.Path);

        var screen = app.Children[0];
        Assert.Equal("Screen", screen.Type);
        Assert.Equal("CourtView/General Application", screen.Path);

        var nested = screen.Children[1].Children[0];
        Assert.Equal("Close All Modules Menu Option", nested.Name);
        Assert.Equal("Tags", nested.TaxonomyType);
        Assert.Equal("CourtView/General Application/File Menu Option/Close All Modules Menu Option", nested.Path);
    }

    [Fact]
    public async Task MaxDepthOne_ReturnsAppsOnly() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, maxDepth: 1);
        var app = Assert.Single(CliToolFixtures.Data<OrPayload>(result)!.Tree);

        Assert.Equal("CourtView", app.Name);
        Assert.Empty(app.Children);
    }

    [Fact]
    public async Task MaxDepthTwo_ReturnsAppsAndScreensWithoutElements() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, maxDepth: 2);
        var app = Assert.Single(CliToolFixtures.Data<OrPayload>(result)!.Tree);
        var screen = Assert.Single(app.Children);

        Assert.Equal("General Application", screen.Name);
        Assert.Empty(screen.Children);
    }

    [Fact]
    public async Task Query_FiltersNodesByName_ButKeepsAncestorsNavigable() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, query: "Close");
        var data = CliToolFixtures.Data<OrPayload>(result)!;

        // Two nodes match ("Close" and "Close All Modules Menu Option"); the app and screen are
        // kept only because a descendant matched, so the tree stays navigable.
        Assert.Equal(2, data.Returned);
        var app = Assert.Single(data.Tree);
        Assert.Equal("CourtView", app.Name);
        var screen = Assert.Single(app.Children);
        var elements = screen.Children;
        Assert.Equal(2, elements.Count);
        Assert.Contains(elements, e => e.Name == "Close");
        Assert.Contains(elements, e => e.Name == "File Menu Option");
    }

    [Fact]
    public async Task QueryWithNoMatch_ReturnsAnEmptyTree() {
        var (sut, _) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, query: "NoSuchElement");
        var data = CliToolFixtures.Data<OrPayload>(result)!;

        Assert.Equal(0, data.Returned);
        Assert.Empty(data.Tree);
        Assert.Equal(5, data.Total);
    }

    [Fact]
    public async Task EmptyRepository_WarnsInsteadOfFailing() {
        // Live shape for a project with no captured targets.
        var (sut, _) = CreateSut("""{"Result":"Success","Code":"ToolResult","Data":[]}""");

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);

        Assert.Equal("success", result.Status);
        Assert.Equal(0, CliToolFixtures.Data<OrPayload>(result)!.Total);
        Assert.Contains(result.Warnings, w => w.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadOnlyVerb_NeedsNoEnableMutatingCommands() {
        var (sut, cli) = CreateSut();

        await sut.GetObjectRepository(CliToolFixtures.ProjectPath);

        Assert.Single(cli.Calls);
    }

    [Fact]
    public async Task LibrarySource_PassesOneCommaSeparatedFlag() {
        var (sut, cli) = CreateSut("""{"Result":"Success","Data":{"Acme.UiLib":[{"name":"A","type":"App","children":[]}]}}""");
        var paths = Path.Combine(CliToolFixtures.ProjectPath, "libs", "Acme.UiLib.1.2.0.nupkg");

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, source: "library", libraryPaths: paths);

        Assert.Equal("success", result.Status);
        Assert.Equal(paths, CliToolFixtures.TokenAfter(cli.LastTokens, "--library-paths"));
        Assert.Equal("library", CliToolFixtures.Data<LibraryOrPayload>(result)!.Source);
    }

    [Fact]
    public async Task LibrarySource_WithoutLibraryPaths_IsRefused() {
        var (sut, cli) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, source: "library");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "INVALID_ARGUMENT");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task LibrarySource_OutsideAllowedRoots_IsRefused() {
        var cli = new RecordingStructuredCli();
        var filesystem = CliToolFixtures.ProjectOnlyFilesystem();
        var sut = new GetObjectRepositoryTool(cli, filesystem, CliToolFixtures.Policy());

        var result = await sut.GetObjectRepository(
            CliToolFixtures.ProjectPath, source: "library", libraryPaths: @"C:\elsewhere\Acme.UiLib.nupkg");

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PATH_NOT_ALLOWED");
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task LibrarySource_NoLibraryCarriedARepository_Warns() {
        var (sut, _) = CreateSut("""{"Result":"Success","Data":[]}""");
        var paths = Path.Combine(CliToolFixtures.ProjectPath, "libs", "Empty.nupkg");

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, source: "library", libraryPaths: paths);

        Assert.Equal("success", result.Status);
        Assert.Contains(result.Warnings, w => w.Contains("No library carried", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownSource_IsRefused() {
        var (sut, cli) = CreateSut();

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath, source: "tenant");

        Assert.Equal("error", result.Status);
        Assert.Empty(cli.Calls);
    }

    [Fact]
    public async Task FailureEnvelope_ReturnsStructuredErrorWithAnOpenProjectHint() {
        var (sut, _) = CreateSut("""{"Result":"Failure","Message":"No project is open."}""");

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        var error = Assert.Single(result.ErrorDetails);
        Assert.Contains("No project is open", error.Message);
        Assert.Contains("open project", error.FixHint);
        Assert.Equal("find_activity", error.SuggestedTool);
    }

    [Fact]
    public async Task MissingProjectJson_IsRefused() {
        var cli = new RecordingStructuredCli();
        var sut = new GetObjectRepositoryTool(cli, new FakeFilesystemProvider { ProjectJson = null }, CliToolFixtures.Policy());

        var result = await sut.GetObjectRepository(CliToolFixtures.ProjectPath);

        Assert.Equal("error", result.Status);
        Assert.Contains(result.ErrorDetails, e => e.ErrorCode == "PROJECT_JSON_NOT_FOUND");
    }

    [Fact]
    public async Task TimeoutIsClamped() {
        var (sut, cli) = CreateSut();

        await sut.GetObjectRepository(CliToolFixtures.ProjectPath, timeoutSeconds: 999999);

        Assert.Equal(CliToolSupport.MaxTimeoutSeconds, cli.Timeouts[^1]);
    }

    private sealed record OrNode(
        string Name, string Type, string? TaxonomyType, string? Description,
        string? Reference, string Path, int Depth, List<OrNode> Children);

    private sealed record OrPayload(
        string Source, int Apps, int Screens, int Elements, int Total, int Returned,
        string Command, List<OrNode> Tree, string Note);

    private sealed record LibraryEntry(
        string? Library, int Apps, int Screens, int Elements, int Total, List<OrNode> Tree);

    private sealed record LibraryOrPayload(string Source, string Command, List<LibraryEntry> Libraries, string Note);
}
