using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class ProjectFilePolicyTests {
    [Theory]
    [InlineData("notes.md")]
    [InlineData("config.json")]
    [InlineData("readme.txt")]
    public void AllowedExtensions_AreAccepted(string relativePath) {
        Assert.True(ProjectFilePolicy.IsAllowedExtension(relativePath));
        Assert.Null(ProjectFilePolicy.ValidateMutatingFile(relativePath, "{}", requireContent: true));
    }

    [Fact]
    public void RejectsDisallowedExtension() {
        Assert.False(ProjectFilePolicy.IsAllowedExtension("Main.xaml"));
        Assert.NotNull(ProjectFilePolicy.ValidateMutatingFile("Main.xaml", "<x/>", requireContent: true));
    }

    [Theory]
    [InlineData("project.json")]
    [InlineData("docs/implementation-plan.json")]
    [InlineData("docs/implementation-plan.md")]
    [InlineData("docs/knowledge/retry.md")]
    [InlineData("docs/adr/0001-queues.md")]
    public void ReservedPaths_AreRejected(string relativePath) {
        Assert.True(ProjectFilePolicy.IsReservedPath(relativePath));
        Assert.NotNull(ProjectFilePolicy.ValidateMutatingFile(relativePath, "# x", requireContent: true));
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData("orchestrator-credentials.json")]
    [InlineData("server.pem")]
    [InlineData("id_rsa.key")]
    public void SecretNames_AreRejected(string relativePath) {
        Assert.True(ProjectFilePolicy.IsSecretName(relativePath));
        Assert.NotNull(ProjectFilePolicy.ValidateMutatingFile(relativePath, "x", requireContent: true));
    }

    [Fact]
    public void RedactedBody_IsRejected() {
        Assert.NotNull(ProjectFilePolicy.ValidateMutatingFile("notes.md", "token=***REDACTED***", requireContent: true));
    }

    [Fact]
    public void InvalidJson_IsRejected() {
        var error = ProjectFilePolicy.ValidateMutatingFile("settings.json", "{ not json", requireContent: true);
        Assert.NotNull(error);
        Assert.Contains("JSON", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidJson_IsAccepted() {
        Assert.Null(ProjectFilePolicy.ValidateMutatingFile("settings.json", """{ "a": 1 }""", requireContent: true));
    }
}
