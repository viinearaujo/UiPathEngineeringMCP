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
}
