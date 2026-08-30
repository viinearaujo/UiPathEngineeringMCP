using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ActivityFindParserTests
{
    [Fact]
    public void Parse_ArrayOfActivities_ReadsNameAndPackage()
    {
        const string json = """
            [
              { "name": "Click", "fullName": "UiPath.Core.Activities.Click", "package": "UiPath.UIAutomation.Activities", "packageVersion": "[24.10.3]" }
            ]
            """;
        var hit = Assert.Single(ActivityFindParser.Parse(json));
        Assert.Equal("Click", hit.Name);
        Assert.Equal("UiPath.Core.Activities.Click", hit.FullTypeName);
        Assert.Equal("UiPath.UIAutomation.Activities", hit.PackageId);
        Assert.Equal("24.10.3", hit.PackageVersion);
    }

    [Fact]
    public void Parse_EnvelopeWithActivitiesProperty_CollectsHits()
    {
        const string json = """
            { "Result": "Success", "activities": [
              { "activityClassName": "UiPath.Excel.Activities.Business.ReadRangeX", "packageId": "UiPath.Excel.Activities", "version": "3.5.0" }
            ]}
            """;
        var hit = Assert.Single(ActivityFindParser.Parse(json));
        Assert.Equal("ReadRangeX", hit.Name);
        Assert.Equal("UiPath.Excel.Activities", hit.PackageId);
    }

    [Fact]
    public void Parse_GenericSwitchName_StripsArity()
    {
        Assert.Equal("Switch", ActivityFindParser.ShortName("System.Activities.Statements.Switch`1"));
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty() =>
        Assert.Empty(ActivityFindParser.Parse("{ not json"));

    [Fact]
    public void Parse_PropertiesArray_MapsKinds()
    {
        const string json = """
            { "name": "Assign", "properties": [
              { "name": "To", "required": true, "kind": "Expression" },
              { "name": "DisplayName", "required": false, "kind": "Literal" }
            ]}
            """;
        var hit = Assert.Single(ActivityFindParser.Parse(json));
        Assert.Equal(2, hit.Properties!.Count);
        Assert.Equal(PropertyKind.Expression, hit.Properties[0].Kind);
        Assert.True(hit.Properties[0].Required);
    }
}

public class ActivityCatalogResolverTests
{
    [Fact]
    public async Task ResolveAsync_NoProject_ReturnsFallback()
    {
        var resolver = new ActivityCatalogResolver();
        var catalog = await resolver.ResolveAsync(null);
        Assert.Equal("fallback", catalog.Source);
        Assert.True(catalog.TryGet("Sequence", out _));
        Assert.True(catalog.TryGet("Switch", out _));
    }

    [Fact]
    public async Task ResolveAsync_MergesDiscoveredActivitiesAndStampsPackageVersion()
    {
        var fs = new MemoryFilesystem
        {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.Excel.Activities": "[3.5.0]", "UiPath.System.Activities": "[26.4.0]" } }"""
        };
        var discovery = new StubDiscovery
        {
            Hits =
            [
                new DiscoveredActivity("ReadRangeX", "UiPath.Excel.Activities.Business.ReadRangeX",
                    "UiPath.Excel.Activities", "3.5.0")
            ]
        };
        var resolver = new ActivityCatalogResolver(fs, discovery);

        var catalog = await resolver.ResolveAsync("/p");

        Assert.Equal("cli", catalog.Source);
        Assert.True(catalog.TryGet("ReadRangeX", out var excel));
        Assert.Equal("3.5.0", excel!.PackageVersion);
        Assert.True(catalog.TryGet("LogMessage", out var log));
        Assert.Equal("26.4.0", log!.PackageVersion);
        Assert.Equal("UiPath.System.Activities", log.PackageId);
    }

    [Fact]
    public async Task ResolveAsync_DiscoveryThrows_FallsBackToStaticCatalog()
    {
        var fs = new MemoryFilesystem
        {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.System.Activities": "26.4.0" } }"""
        };
        var discovery = new StubDiscovery { ToThrow = new InvalidOperationException("cli missing") };
        var resolver = new ActivityCatalogResolver(fs, discovery);

        var catalog = await resolver.ResolveAsync("/p");

        Assert.True(catalog.TryGet("If", out _));
        Assert.False(catalog.TryGet("Click", out _));
    }

    [Fact]
    public async Task RecommendAsync_LimitsToFiveAndRanksExactNameFirst()
    {
        var resolver = new ActivityCatalogResolver();
        var hits = await resolver.RecommendAsync("LogMessage", projectPath: null, limit: 5);
        Assert.True(hits.Count <= 5);
        Assert.Equal("LogMessage", hits[0].Name);
        Assert.Contains("Message", hits[0].RequiredProperties);
    }

    [Fact]
    public void Rank_TokenQuery_MatchesReadRange()
    {
        var ranked = ActivityCatalogResolver.Rank("read range", ActivityCatalog.All, new Dictionary<string, string>());
        Assert.Contains(ranked, r => r.Name == "ReadRange");
    }

    private sealed class StubDiscovery : IActivityDiscovery
    {
        public IReadOnlyList<DiscoveredActivity> Hits { get; set; } = [];
        public Exception? ToThrow { get; set; }

        public Task<IReadOnlyList<DiscoveredActivity>> FindAsync(string projectPath, string query, CancellationToken cancellationToken = default)
        {
            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            return Task.FromResult(Hits);
        }
    }

    private sealed class MemoryFilesystem : UiPath.Engineering.Mcp.Core.Abstractions.IFilesystemProvider
    {
        public string? ProjectJsonPath { get; set; }
        public string ProjectJson { get; set; } = "{}";

        public bool IsPathAllowed(string requestedPath) => true;
        public string? FindProjectJson(string projectPath) => ProjectJsonPath;
        public IReadOnlyList<string> FindXamlFiles(string projectPath) => [];
        public IReadOnlyList<string> FindCSharpFiles(string projectPath) => [];
        public string ReadAllText(string filePath) => ProjectJson;
        public long GetFileSize(string filePath) => ProjectJson.Length;
        public DateTime GetLastWriteTimeUtc(string filePath) => DateTime.UnixEpoch;
        public UiPath.Engineering.Mcp.Core.Models.DirectoryTreeNode GetDirectoryTree(string root, int maxDepth = 3) => new();
        public void CreateDirectory(string path) { }
        public void WriteAllText(string filePath, string content) { }
        public void DeleteFile(string filePath) { }
        public bool FileExists(string path) => true;
    }
}

public class XamlCatalogGuardTests
{
    [Fact]
    public void FindUnknownActivities_KnownSequence_Empty()
    {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities">
              <Sequence />
            </Activity>
            """;
        Assert.Empty(XamlCatalogGuard.FindUnknownActivities(xaml, ActivityCatalog.Fallback));
    }

    [Fact]
    public void FindUnknownActivities_Click_ReportsUnknown()
    {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities"
                      xmlns:ui="http://schemas.uipath.com/workflow/activities">
              <ui:Click />
            </Activity>
            """;
        var errors = XamlCatalogGuard.FindUnknownActivities(xaml, ActivityCatalog.Fallback);
        Assert.Contains(errors, e => e.Message.Contains("Click"));
    }
}
