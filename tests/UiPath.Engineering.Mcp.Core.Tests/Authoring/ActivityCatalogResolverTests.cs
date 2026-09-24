using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ActivityFindParserTests {
    [Fact]
    public void Parse_ArrayOfActivities_ReadsNameAndPackage() {
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
    public void Parse_EnvelopeWithActivitiesProperty_CollectsHits() {
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
    public void Parse_GenericSwitchName_StripsArity() {
        Assert.Equal("Switch", ActivityFindParser.ShortName("System.Activities.Statements.Switch`1"));
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsEmpty() =>
        Assert.Empty(ActivityFindParser.Parse("{ not json"));

    [Fact]
    public void Parse_PropertiesArray_MapsKinds() {
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

public class ActivityCatalogResolverTests {
    [Fact]
    public async Task ResolveAsync_NoProject_ReturnsFallback() {
        var resolver = new ActivityCatalogResolver();
        var catalog = await resolver.ResolveAsync(null);
        Assert.Equal("fallback", catalog.Source);
        Assert.True(catalog.TryGet("Sequence", out _));
        Assert.True(catalog.TryGet("Switch", out _));
    }

    [Fact]
    public async Task ResolveAsync_MergesDiscoveredActivitiesAndStampsPackageVersion() {
        var fs = new MemoryFilesystem {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.Excel.Activities": "[3.5.0]", "UiPath.System.Activities": "[26.4.0]" } }"""
        };
        var discovery = new StubDiscovery {
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
    public async Task ResolveAsync_DiscoveryThrows_FallsBackToStaticCatalog() {
        var fs = new MemoryFilesystem {
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
    public async Task RecommendAsync_LimitsToFiveAndRanksExactNameFirst() {
        var resolver = new ActivityCatalogResolver();
        var hits = await resolver.RecommendAsync("LogMessage", projectPath: null, limit: 5);
        Assert.True(hits.Count <= 5);
        Assert.Equal("LogMessage", hits[0].Name);
        Assert.Contains("Message", hits[0].RequiredProperties);
    }

    [Fact]
    public void Rank_TokenQuery_MatchesReadRange() {
        var ranked = ActivityCatalogResolver.Rank("read range", ActivityCatalog.All, new Dictionary<string, string>());
        Assert.Contains(ranked, r => r.Name == "ReadRange");
    }

    [Fact]
    public async Task ResolveAsync_EnrichesDiscoveredActivityWithItsPackageStarter() {
        var fs = new MemoryFilesystem {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.UIAutomation.Activities": "[25.10.0]" } }"""
        };
        var discovery = new StubDiscovery {
            Hits = [new DiscoveredActivity("Click", "UiPath.UIAutomationNext.Activities.NClick",
                "UiPath.UIAutomation.Activities", "25.10.0")]
        };
        discovery.DefaultXaml["UiPath.UIAutomationNext.Activities.NClick"] = """
            <uix:NClick ClickType="Single" HealingAgentBehavior="SameAsCard"
                        xmlns:uix="http://schemas.uipath.com/workflow/activities/uix" />
            """;
        var resolver = new ActivityCatalogResolver(fs, discovery);

        var catalog = await resolver.ResolveAsync("/p");

        Assert.True(catalog.TryGet("Click", out var click));
        Assert.Contains(click!.Properties, p => p.Name == "ClickType");
        Assert.Contains(click.Properties, p => p.Name == "HealingAgentBehavior");
        Assert.Equal("http://schemas.uipath.com/workflow/activities/uix", click.XmlNamespace);
        Assert.Equal("uix", click.Prefix);
    }

    [Fact]
    public async Task ResolveAsync_DoesNotQueryStarterForCardActivities() {
        var fs = new MemoryFilesystem {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.System.Activities": "[26.4.0]" } }"""
        };
        var discovery = new StubDiscovery {
            Hits = [new DiscoveredActivity("LogMessage", "UiPath.Core.Activities.LogMessage",
                "UiPath.System.Activities", "26.4.0")]
        };
        var resolver = new ActivityCatalogResolver(fs, discovery);

        await resolver.ResolveAsync("/p");

        Assert.Empty(discovery.DefaultXamlQueries);
    }

    [Fact]
    public async Task ResolveAsync_DiscoveryFailure_SkipsStarterLookups() {
        var fs = new MemoryFilesystem {
            ProjectJsonPath = "/p/project.json",
            ProjectJson = """{ "name": "P", "dependencies": { "UiPath.System.Activities": "26.4.0" } }"""
        };
        var discovery = new StubDiscovery { ToThrow = new InvalidOperationException("cli missing") };
        var resolver = new ActivityCatalogResolver(fs, discovery);

        await resolver.ResolveAsync("/p");

        Assert.Empty(discovery.DefaultXamlQueries);
    }

    [Fact]
    public void TruncatedPackages_ReportsPackagesBeyondTheBudget() {
        var packages = Enumerable.Range(0, ActivityCatalogResolver.MaxPackageQueries + 3)
            .Select(i => $"UiPath.Package{i}.Activities")
            .ToList();

        var truncated = ActivityCatalogResolver.TruncatedPackages(packages);
        var queried = ActivityCatalogResolver.DiscoveryQueries(packages).Skip(1).ToList();
        Assert.Equal(3, truncated.Count);
        Assert.Equal(ActivityCatalogResolver.MaxPackageQueries, queried.Count);
        Assert.DoesNotContain(truncated[0], queried);
    }

    [Fact]
    public void DiscoveryWarning_SurfacesTruncatedPackages() {
        var catalog = ActivityCatalogResolver.Merge(
            ActivityCatalog.All,
            new Dictionary<string, string>(),
            [],
            "cli",
            discoveryFailed: false,
            truncatedPackages: ["UiPath.Extra.Activities"]);

        var warning = ActivityCatalogResolver.DiscoveryWarning(catalog);
        Assert.NotNull(warning);
        Assert.Contains("UiPath.Extra.Activities", warning);
    }

    [Fact]
    public void DefaultXamlParser_ReadsPropertiesNamespaceAndBodySlot() {
        const string starter = """
            <ui:ForEachRow DataTable="{x:Null}" DisplayName="For Each Row"
                xmlns:ui="http://schemas.uipath.com/workflow/activities"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ui:ForEachRow.Body>
                <ActivityAction x:TypeArguments="sd:DataRow">
                  <Sequence />
                </ActivityAction>
              </ui:ForEachRow.Body>
            </ui:ForEachRow>
            """;

        var surface = DefaultXamlParser.Parse(starter);

        Assert.NotNull(surface);
        Assert.Equal("ForEachRow", surface!.ElementName);
        Assert.Equal("http://schemas.uipath.com/workflow/activities", surface.XmlNamespace);
        Assert.True(surface.IsContainer);
        Assert.Contains(surface.Properties, p => p.Name == "DataTable");
        Assert.Contains(surface.Properties, p => p.Name == "DisplayName");
        // The body slot is a container descriptor, never a settable property.
        Assert.DoesNotContain(surface.Properties, p => p.Name == "Body");
    }

    [Fact]
    public void DefaultXamlParser_ContentPropertyChild_MarksContainer() {
        const string starter = """
            <ui:ForEach x:TypeArguments="x:String" DisplayName="For Each"
                xmlns:ui="http://schemas.uipath.com/workflow/activities"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <ActivityAction x:TypeArguments="x:String" />
            </ui:ForEach>
            """;

        var surface = DefaultXamlParser.Parse(starter);

        Assert.NotNull(surface);
        Assert.True(surface!.IsContainer);
        Assert.Contains(surface.Properties, p => p.Name == "TypeArgument");
    }

    [Fact]
    public void DefaultXamlParser_ExtractsFromEnvelope() {
        const string envelope = """
            {"Result":"Success","Data":{"defaultXaml":"<uix:NClick ClickType=\"Single\" xmlns:uix=\"http://schemas.uipath.com/workflow/activities/uix\" />"}}
            """;

        var xaml = DefaultXamlParser.ExtractXaml(envelope);

        Assert.NotNull(xaml);
        Assert.Contains("NClick", xaml);
    }

    [Fact]
    public void DefaultXamlParser_InvalidXaml_ReturnsNull() =>
        Assert.Null(DefaultXamlParser.Parse("<not"));

    private sealed class StubDiscovery : IActivityDiscovery {
        public IReadOnlyList<DiscoveredActivity> Hits { get; set; } = [];
        public Exception? ToThrow { get; set; }
        public Dictionary<string, string> DefaultXaml { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> DefaultXamlQueries { get; } = [];

        public Task<IReadOnlyList<DiscoveredActivity>> FindAsync(string projectPath, string query, CancellationToken cancellationToken = default) {
            if (ToThrow is not null) {
                throw ToThrow;
            }

            return Task.FromResult(Hits);
        }

        public Task<string?> GetDefaultXamlAsync(string projectPath, string activityClassName, CancellationToken cancellationToken = default) {
            DefaultXamlQueries.Add(activityClassName);
            return Task.FromResult(DefaultXaml.TryGetValue(activityClassName, out var xaml) ? xaml : null);
        }
    }

    private sealed class MemoryFilesystem : UiPath.Engineering.Mcp.Core.Abstractions.IFilesystemProvider {
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

public class XamlCatalogGuardTests {
    [Fact]
    public void FindUnknownActivities_KnownSequence_Empty() {
        const string xaml = """
            <Activity xmlns="http://schemas.microsoft.com/netfx/2009/xaml/activities">
              <Sequence />
            </Activity>
            """;
        Assert.Empty(XamlCatalogGuard.FindUnknownActivities(xaml, ActivityCatalog.Fallback));
    }

    [Fact]
    public void FindUnknownActivities_Click_ReportsUnknown() {
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
