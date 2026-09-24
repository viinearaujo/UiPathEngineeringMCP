using System.Text.Json;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class ObjectRepositoryParserTests {
    // Live shape from `uip rpa get-object-repository --output json`.
    private const string ProjectPayload = """
        [
          {
            "name": "CourtView",
            "description": "",
            "type": "App",
            "reference": "GaaASJaYQUmehs0191pyjw/ocamH7Eo",
            "children": [
              {
                "name": "General Application",
                "type": "Screen",
                "reference": "ref-screen",
                "children": [
                  {
                    "name": "Close",
                    "taxonomyType": "Button",
                    "type": "Element",
                    "reference": "ref-close",
                    "children": []
                  },
                  {
                    "name": "File Menu Option",
                    "taxonomyType": "Tags",
                    "type": "Element",
                    "reference": "ref-menu",
                    "children": [
                      {
                        "name": "Close All Modules Menu Option",
                        "taxonomyType": "Tags",
                        "type": "Element",
                        "reference": "ref-nested",
                        "children": []
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
        """;

    [Fact]
    public void ParseProject_ReadsAppsScreensAndElements() {
        using var document = JsonDocument.Parse(ProjectPayload);

        var result = ObjectRepositoryParser.ParseProject(document.RootElement);

        Assert.Equal(1, result.Apps);
        Assert.Equal(1, result.Screens);
        Assert.Equal(3, result.Elements);
        Assert.Equal(5, result.Flat.Count);
    }

    [Fact]
    public void ParseProject_ReadsNodeFields() {
        using var document = JsonDocument.Parse(ProjectPayload);

        var app = Assert.Single(ObjectRepositoryParser.ParseProject(document.RootElement).Nodes);

        Assert.Equal("CourtView", app.Name);
        Assert.Equal("App", app.Type);
        Assert.Equal("GaaASJaYQUmehs0191pyjw/ocamH7Eo", app.Reference);
        Assert.Equal("CourtView", app.Path);
        Assert.Equal(0, app.Depth);
    }

    [Fact]
    public void ParseProject_NestedElementsCarryDottedPathAndDepth() {
        using var document = JsonDocument.Parse(ProjectPayload);

        var result = ObjectRepositoryParser.ParseProject(document.RootElement);
        var nested = result.Flat.Single(n => n.Name == "Close All Modules Menu Option");

        Assert.Equal("CourtView/General Application/File Menu Option/Close All Modules Menu Option", nested.Path);
        Assert.Equal(3, nested.Depth);
        Assert.Equal("Tags", nested.TaxonomyType);
    }

    [Fact]
    public void ParseProject_EmptyRepository_ReturnsNoNodes() {
        // Live shape for a project with no captured targets: {"Result":"Success","Data":[]}.
        using var document = JsonDocument.Parse("[]");

        var result = ObjectRepositoryParser.ParseProject(document.RootElement);

        Assert.Equal(0, result.Apps);
        Assert.Empty(result.Flat);
    }

    [Fact]
    public void ParseProject_NullData_ReturnsEmptyResult() {
        var result = ObjectRepositoryParser.ParseProject(null);

        Assert.Empty(result.Nodes);
        Assert.Equal(0, result.Elements);
    }

    [Fact]
    public void ParseProject_PascalCasePayload_IsReadToo() {
        using var document = JsonDocument.Parse("""
            [{"Name":"App","Type":"App","Reference":"r","Children":[{"Name":"S","Type":"Screen","Children":[]}]}]
            """);

        var result = ObjectRepositoryParser.ParseProject(document.RootElement);

        Assert.Equal(1, result.Apps);
        Assert.Equal(1, result.Screens);
    }

    [Fact]
    public void ParseLibraries_KeyedByLibrary_ReturnsOneResultPerLibrary() {
        using var document = JsonDocument.Parse("""
            {
              "Acme.UiLib": [{"name":"AcmeApp","type":"App","reference":"a","children":[]}],
              "Other.UiLib": [{"name":"OtherApp","type":"App","reference":"b","children":[{"name":"Screen1","type":"Screen","children":[]}]}]
            }
            """);

        var results = ObjectRepositoryParser.ParseLibraries(document.RootElement);

        Assert.Equal(2, results.Count);
        Assert.Equal("Acme.UiLib", results[0].Library);
        Assert.Equal(1, results[0].Apps);
        Assert.Equal("Other.UiLib", results[1].Library);
        Assert.Equal(1, results[1].Screens);
    }

    [Fact]
    public void ParseLibraries_LibrariesArray_ReturnsOneResultPerEntry() {
        using var document = JsonDocument.Parse("""
            {"libraries":[{"libraryName":"Acme.UiLib","nodes":[{"name":"AcmeApp","type":"App","children":[]}]}]}
            """);

        var results = ObjectRepositoryParser.ParseLibraries(document.RootElement);

        var result = Assert.Single(results);
        Assert.Equal("Acme.UiLib", result.Library);
        Assert.Equal(1, result.Apps);
    }

    [Fact]
    public void ParseLibraries_ArrayOfGroups_ReturnsOneResultPerEntry() {
        using var document = JsonDocument.Parse("""
            [{"library":"Acme.UiLib","objectRepository":[{"name":"AcmeApp","type":"App","children":[]}]}]
            """);

        var results = ObjectRepositoryParser.ParseLibraries(document.RootElement);

        var result = Assert.Single(results);
        Assert.Equal("Acme.UiLib", result.Library);
    }

    [Fact]
    public void ParseLibraries_FlatNodeArray_IsReadAsOneAnonymousResult() {
        using var document = JsonDocument.Parse(ProjectPayload);

        var results = ObjectRepositoryParser.ParseLibraries(document.RootElement);

        var result = Assert.Single(results);
        Assert.Null(result.Library);
        Assert.Equal(1, result.Apps);
    }

    [Fact]
    public void ParseLibraries_EmptyPayload_ReturnsNoLibraries() {
        // Packages without an Object Repository are omitted by the CLI.
        using var document = JsonDocument.Parse("[]");

        Assert.Empty(ObjectRepositoryParser.ParseLibraries(document.RootElement));
        Assert.Empty(ObjectRepositoryParser.ParseLibraries(null));
    }
}

public class PackagesParserTests {
    [Fact]
    public void ParseVersions_ReadsIdFlagAndList() {
        using var document = JsonDocument.Parse("""
            {"packageId":"UiPath.Excel.Activities","includePrerelease":true,"versions":["3.6.1","3.6.0-preview","3.5.3"]}
            """);

        var versions = PackagesParser.ParseVersions(document.RootElement);

        Assert.Equal("UiPath.Excel.Activities", versions.PackageId);
        Assert.True(versions.IncludePrerelease);
        Assert.Equal(3, versions.Versions.Count);
        Assert.Equal("3.6.1", versions.Latest);
        Assert.Equal("3.6.1", versions.LatestStable);
    }

    [Fact]
    public void ParseVersions_LatestStable_SkipsPrerelease() {
        using var document = JsonDocument.Parse("""
            {"packageId":"P","includePrerelease":true,"versions":["4.0.0-preview","3.9.1"]}
            """);

        var versions = PackagesParser.ParseVersions(document.RootElement);

        Assert.Equal("4.0.0-preview", versions.Latest);
        Assert.Equal("3.9.1", versions.LatestStable);
    }

    [Fact]
    public void ParseVersions_NoVersions_LeavesLatestNull() {
        using var document = JsonDocument.Parse("""{"packageId":"P","versions":[]}""");

        var versions = PackagesParser.ParseVersions(document.RootElement);

        Assert.Null(versions.Latest);
        Assert.Null(versions.LatestStable);
    }

    [Fact]
    public void ParseVersions_ObjectItems_ReadTheVersionField() {
        using var document = JsonDocument.Parse("""
            {"packageId":"P","versions":[{"version":"1.0.0"},{"version":"0.9.0"}]}
            """);

        Assert.Equal(new[] { "1.0.0", "0.9.0" }, PackagesParser.ParseVersions(document.RootElement).Versions);
    }

    [Fact]
    public void ParseVersions_MissingData_ReturnsEmptyResult() {
        var versions = PackagesParser.ParseVersions(null);

        Assert.Empty(versions.Versions);
        Assert.Equal(string.Empty, versions.PackageId);
    }

    [Fact]
    public void ParseInstall_NoFailures_Succeeds() {
        using var document = JsonDocument.Parse("""{"failedPackages":[]}""");

        var outcome = PackagesParser.ParseInstall(document.RootElement, ["id=UiPath.Excel.Activities"], null);

        Assert.True(outcome.Succeeded);
        Assert.Equal("id=UiPath.Excel.Activities", Assert.Single(outcome.Requested));
    }

    [Fact]
    public void ParseInstall_FailedPackages_FormatsIdVersionAndReason() {
        using var document = JsonDocument.Parse("""
            {"failedPackages":[{"id":"UiPath.Fake","version":"1.0.0","message":"Package not found on the feed."}]}
            """);

        var outcome = PackagesParser.ParseInstall(document.RootElement, ["id=UiPath.Fake,version=1.0.0"], null);

        Assert.False(outcome.Succeeded);
        Assert.Equal("UiPath.Fake@1.0.0: Package not found on the feed.", Assert.Single(outcome.Failed));
    }

    [Fact]
    public void ParseInstall_StringFailures_AreKeptVerbatim() {
        using var document = JsonDocument.Parse("""{"failed":["UiPath.Fake"]}""");

        var outcome = PackagesParser.ParseInstall(document.RootElement, [], null);

        Assert.Equal("UiPath.Fake", Assert.Single(outcome.Failed));
    }

    [Fact]
    public void ParseInstall_NoData_ReportsRequestedOnly() {
        var outcome = PackagesParser.ParseInstall(null, ["id=P"], "envelope message");

        Assert.True(outcome.Succeeded);
        Assert.Equal("envelope message", outcome.Message);
    }

    [Fact]
    public void FormatPackageSpec_OmittedVersion_ResolvesLatestCompatible() {
        Assert.Equal("id=UiPath.Excel.Activities", PackagesParser.FormatPackageSpec("UiPath.Excel.Activities", null));
        Assert.Equal("id=UiPath.Excel.Activities", PackagesParser.FormatPackageSpec("UiPath.Excel.Activities", "  "));
    }

    [Fact]
    public void FormatPackageSpec_PinnedVersion_IsCommaJoined() {
        Assert.Equal(
            "id=UiPath.System.Activities,version=23.10.1",
            PackagesParser.FormatPackageSpec("UiPath.System.Activities", "23.10.1"));
    }

    [Fact]
    public void ReadMarkdown_ReadsStringAndObjectPayloads() {
        using var stringPayload = JsonDocument.Parse("\"# API\\n\\nTypes...\"");
        using var objectPayload = JsonDocument.Parse("""{"markdown":"# API"}""");

        Assert.Equal("# API\n\nTypes...", PackagesParser.ReadMarkdown(stringPayload.RootElement));
        Assert.Equal("# API", PackagesParser.ReadMarkdown(objectPayload.RootElement));
        Assert.Null(PackagesParser.ReadMarkdown(null));
    }
}
