using UiPath.Engineering.Mcp.Core.Configuration;
using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliCommandPolicyTests {
    private static CliCommandPolicy CreateSut(Action<UiPathCliOptions>? configure = null) {
        var options = new UiPathCliOptions();
        configure?.Invoke(options);
        return new CliCommandPolicy(options);
    }

    [Fact]
    public void Classify_ReadOnlySubcommand_IsAllowedReadOnly() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly,
            sut.Classify("rpa", "validate --project-dir \"C:/proj\" --output json"));
    }

    [Fact]
    public void Classify_VerbMatchingIsCaseInsensitive() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly, sut.Classify("RPA", "VALIDATE --project-dir x"));
    }

    [Fact]
    public void Classify_KnownMutatingSubcommand_IsAllowedMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("solution", "publish --output json"));
    }

    [Fact]
    public void Classify_UnknownSubcommand_FailsClosedAsMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "some-brand-new-verb"));
    }

    [Fact]
    public void Classify_EmptyArguments_FailsClosedAsMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "   "));
    }

    [Fact]
    public void Classify_VerbOutsideAllowlist_IsVerbNotAllowed() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.VerbNotAllowed, sut.Classify("orx", "assets list"));
    }

    [Theory]
    [InlineData("validate --output json & whoami")]
    [InlineData("validate | more")]
    [InlineData("validate > out.txt")]
    [InlineData("validate < in.txt")]
    [InlineData("validate --output %PATH%")]
    [InlineData("validate ^")]
    public void Classify_ShellMetacharacters_AreRejected(string arguments) {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.ArgumentsRejected, sut.Classify("rpa", arguments));
    }

    [Fact]
    public void Classify_QuotedPathWithSpaces_IsNotRejected() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly,
            sut.Classify("rpa", "validate --project-dir \"C:/my proj\" --output json"));
    }

    [Fact]
    public void Classify_SolutionNestedReadOnlySubcommand_PrefixMatches() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedReadOnly,
            sut.Classify("solution", "project list --output json"));
    }

    [Fact]
    public void Classify_SolutionNestedMutatingSubcommand_IsAllowedMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("solution", "project remove x"));
    }

    [Fact]
    public void Classify_PrefixMatchDoesNotMatchLongerToken_IsAllowedMutating() {
        var sut = CreateSut();

        // "validatex" must not prefix-match the "validate" entry.
        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "validatex --output json"));
    }

    [Fact]
    public void Classify_RpaAnalyze_NoLongerReadOnly_IsAllowedMutating() {
        var sut = CreateSut();

        Assert.Equal(CliCommandClass.AllowedMutating, sut.Classify("rpa", "analyze --project-dir x"));
    }
}
