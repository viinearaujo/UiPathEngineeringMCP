using UiPath.Engineering.Mcp.Core.Authoring;

namespace UiPath.Engineering.Mcp.Core.Tests.Authoring;

public class ProjectXamlSettingsTests {
    [Theory]
    [InlineData(null, ExpressionLanguage.VisualBasic)]
    [InlineData("", ExpressionLanguage.VisualBasic)]
    [InlineData("VisualBasic", ExpressionLanguage.VisualBasic)]
    [InlineData("CSharp", ExpressionLanguage.CSharp)]
    [InlineData("csharp", ExpressionLanguage.CSharp)]
    [InlineData("C#", ExpressionLanguage.CSharp)]
    [InlineData("Legacy", ExpressionLanguage.VisualBasic)]
    public void ParseExpressionLanguage_MapsProjectJsonValues(string? value, ExpressionLanguage expected) =>
        Assert.Equal(expected, ProjectXamlSettings.ParseExpressionLanguage(value));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Windows", false)]
    [InlineData("Portable", false)]
    [InlineData("Legacy", true)]
    [InlineData("legacy", true)]
    [InlineData("net461", true)]
    [InlineData(".NETFramework,Version=v4.6.1", true)]
    public void IsLegacyFramework_OnlyLegacyTargetsAreLegacy(string? value, bool expected) =>
        Assert.Equal(expected, ProjectXamlSettings.IsLegacyFramework(value));

    [Fact]
    public void Default_IsVisualBasicOnModernTarget() {
        Assert.Equal(ExpressionLanguage.VisualBasic, ProjectXamlSettings.Default.ExpressionLanguage);
        Assert.False(ProjectXamlSettings.Default.IsCSharp);
        Assert.False(ProjectXamlSettings.Default.IsLegacyTargetFramework);
        Assert.Equal(ProjectXamlSettings.ModernCoreAssembly, ProjectXamlSettings.Default.CoreAssembly);
    }

    [Fact]
    public void CoreAssembly_SwitchesToMscorlibForLegacy() {
        var legacy = ProjectXamlSettings.Default with { IsLegacyTargetFramework = true };
        Assert.Equal(ProjectXamlSettings.LegacyCoreAssembly, legacy.CoreAssembly);
    }

    [Fact]
    public void From_NullModel_ReturnsDefault() =>
        Assert.Equal(ProjectXamlSettings.Default, ProjectXamlSettings.From(null));

    [Fact]
    public void From_Model_ReadsLanguageAndFramework() {
        var settings = ProjectXamlSettings.From(new Models.UiPathProjectModel {
            ExpressionLanguage = "CSharp",
            TargetFramework = "Legacy"
        });

        Assert.Equal(ExpressionLanguage.CSharp, settings.ExpressionLanguage);
        Assert.True(settings.IsCSharp);
        Assert.True(settings.IsLegacyTargetFramework);
    }

    [Fact]
    public async Task ResolveAsync_NullBuilderOrPath_ReturnsDefault() {
        Assert.Equal(ProjectXamlSettings.Default, await ProjectXamlSettings.ResolveAsync(null, "/projects/p"));
        Assert.Equal(ProjectXamlSettings.Default,
            await ProjectXamlSettings.ResolveAsync(new StubBuilder("CSharp", "Portable"), null));
    }

    [Fact]
    public async Task ResolveAsync_ReadsProjectSettings() {
        var settings = await ProjectXamlSettings.ResolveAsync(
            new StubBuilder("CSharp", "Windows"), "/projects/p");

        Assert.True(settings.IsCSharp);
        Assert.False(settings.IsLegacyTargetFramework);
    }

    [Fact]
    public async Task ResolveAsync_UnreadableProject_FallsBackToDefault() {
        var settings = await ProjectXamlSettings.ResolveAsync(
            new StubBuilder(toThrow: new FileNotFoundException()), "/projects/p");

        Assert.Equal(ProjectXamlSettings.Default, settings);
    }

    // Hand-written fake per repo convention: no mocking framework.
    private sealed class StubBuilder : Parsing.IProjectModelBuilder {
        private readonly string? _language;
        private readonly string? _framework;
        private readonly Exception? _toThrow;

        internal StubBuilder(string? language = null, string? framework = null, Exception? toThrow = null) {
            _language = language;
            _framework = framework;
            _toThrow = toThrow;
        }

        public Task<Models.UiPathProjectModel> BuildAsync(string projectPath, CancellationToken cancellationToken = default) =>
            _toThrow is not null
                ? Task.FromException<Models.UiPathProjectModel>(_toThrow)
                : Task.FromResult(new Models.UiPathProjectModel {
                    ProjectPath = projectPath,
                    ExpressionLanguage = _language,
                    TargetFramework = _framework
                });
    }
}
