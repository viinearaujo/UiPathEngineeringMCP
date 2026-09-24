using System.Text.Json;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Core.Authoring;
using UiPath.Engineering.Mcp.Core.Models;
using UiPath.Engineering.Mcp.Core.Parsing;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class GetActivityMetadataToolTests {
    private const string ProjectPath = "/projects/testProcess";

    private static GetActivityMetadataTool Tool(IActivityCatalogResolver? resolver = null) =>
        new(resolver ?? TestCatalogs.Resolver(new FakeFilesystemProvider()), new FakeFilesystemProvider { Allowed = true });

    private static JsonElement Data(ToolResult result) => JsonSerializer.SerializeToElement(result.Data);

    [Fact]
    public async Task KnownActivity_ReportsCamelCaseSurfaceAndBody() {
        var result = await Tool().GetActivityMetadata("RetryScope");

        Assert.Equal("success", result.Status);
        var data = Data(result);

        Assert.Equal("RetryScope", data.GetProperty("name").GetString());
        Assert.Equal("ui", data.GetProperty("prefix").GetString());
        Assert.Equal("UiPath.System.Activities", data.GetProperty("packageId").GetString());
        Assert.True(data.GetProperty("isContainer").GetBoolean());

        // BodyShape.UntypedAction serializes as camelCase.
        Assert.Equal("untypedAction", data.GetProperty("body").GetProperty("shape").GetString());
        Assert.Equal("ActivityBody", data.GetProperty("body").GetProperty("property").GetString());

        var properties = data.GetProperty("properties").EnumerateArray().ToList();
        Assert.Contains(properties, p => p.GetProperty("name").GetString() == "NumberOfRetries");
        Assert.All(properties, p => Assert.True(p.TryGetProperty("kind", out _)));
    }

    [Fact]
    public async Task ExpressionProperty_ReportsKindAndRequiredFlag() {
        var result = await Tool().GetActivityMetadata("If");
        var condition = Data(result).GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Condition");

        Assert.Equal("expression", condition.GetProperty("kind").GetString());
        Assert.True(condition.GetProperty("required").GetBoolean());
        // A curated schema names the property and its kind but does not carry CLR type or
        // direction; only a reflection-completed surface does (see CompleteReflectedSurface).
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("direction").ValueKind);
    }

    [Fact]
    public async Task EnumProperty_ReportsAllowedValues() {
        var result = await Tool().GetActivityMetadata("LogMessage");
        var level = Data(result).GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Level");

        var allowed = level.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.Contains("Info", allowed);
        Assert.Contains("Error", allowed);
    }

    [Fact]
    public async Task Assign_ReportsTypeArgumentKind() {
        var result = await Tool().GetActivityMetadata("Assign");
        var typeArgument = Data(result).GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "TypeArgument");

        Assert.Equal("typeArgument", typeArgument.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ToolboxLabel_ReportsTheEmittedElementName() {
        var result = await Tool().GetActivityMetadata("While");

        Assert.Equal("success", result.Status);
        var data = Data(result);
        Assert.Equal("While", data.GetProperty("name").GetString());
        // The toolbox label and the emitted type differ for the loop wraps.
        Assert.Equal("InterruptibleWhile", data.GetProperty("renderName").GetString());
    }

    [Fact]
    public async Task UnknownActivity_ReturnsStructuredSuggestionError() {
        var result = await Tool().GetActivityMetadata("Logmessag");

        Assert.Equal("error", result.Status);
        Assert.Equal(ToolErrorCodes.ActivityNotFound, result.ErrorDetails[0].ErrorCode);
        Assert.Contains("LogMessage", result.ErrorDetails[0].FixHint);
    }

    [Fact]
    public async Task EmptyName_IsRejected() {
        var result = await Tool().GetActivityMetadata("   ");

        Assert.Equal("error", result.Status);
        Assert.Equal(ToolErrorCodes.InvalidArgument, result.ErrorDetails[0].ErrorCode);
    }

    [Fact]
    public async Task CuratedSurface_WarnsThatRequirednessIsNotAuthoritative() {
        var result = await Tool().GetActivityMetadata("LogMessage");

        Assert.Equal("success", result.Status);
        Assert.False(Data(result).GetProperty("propertiesAreComplete").GetBoolean());
        Assert.Contains(result.Warnings, w => w.Contains("not authoritative", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CompleteReflectedSurface_OmitsTheLenientWarning() {
        var schema = new ActivitySchema(
            "Reflected", "ui", ActivityCatalog.Ui.Ns, false,
            [new PropertySchema("DisplayName", false, PropertyKind.Literal),
             new PropertySchema("Value", true, PropertyKind.Expression, ClrType: "String", Direction: ArgumentDirection.In)],
            PropertiesAreComplete: true);
        var catalog = new ListActivityCatalog([schema], "test");
        var resolver = new StubCatalogResolver(catalog);

        var result = await Tool(resolver).GetActivityMetadata("Reflected");
        var data = Data(result);

        Assert.True(data.GetProperty("propertiesAreComplete").GetBoolean());
        Assert.Empty(result.Warnings);
        Assert.Equal("in", data.GetProperty("properties").EnumerateArray()
            .Single(p => p.GetProperty("name").GetString() == "Value")
            .GetProperty("direction").GetString());
    }

    [Fact]
    public void ToCamel_LowercasesOnlyTheFirstCharacter() {
        Assert.Equal("untypedAction", GetActivityMetadataTool.ToCamel("UntypedAction"));
        Assert.Equal("inOut", GetActivityMetadataTool.ToCamel("InOut"));
        Assert.Equal("expression", GetActivityMetadataTool.ToCamel("Expression"));
        Assert.Equal("activity", GetActivityMetadataTool.ToCamel("activity"));
    }

    private sealed class StubCatalogResolver : IActivityCatalogResolver {
        private readonly IActivityCatalog _catalog;
        public StubCatalogResolver(IActivityCatalog catalog) => _catalog = catalog;

        public Task<IActivityCatalog> ResolveAsync(string? projectPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_catalog);

        public Task<IReadOnlyList<ActivityRecommendation>> RecommendAsync(
            string query, string? projectPath, int limit = 5, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityRecommendation>>([]);
    }
}