using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliExecutableResolverTests {
    private const string PathDir = @"C:\npm";

    // File-exists predicate over a fixed set of full paths (case-insensitive like Windows).
    private static CliExecutableResolver.LaunchSpec? Resolve(string configured, params string[] existingFiles) {
        var files = new HashSet<string>(existingFiles, StringComparer.OrdinalIgnoreCase);
        return CliExecutableResolver.Resolve(configured, [PathDir], files.Contains);
    }

    [Fact]
    public void Resolve_ExplicitExistingFilePath_IsUsedDirectly() {
        var path = @"C:\tools\uip.exe";

        var spec = Resolve(path, path);

        Assert.NotNull(spec);
        Assert.Equal(path, spec.FileName);
        Assert.Equal(path, spec.ResolvedPath);
        Assert.Equal(string.Empty, spec.ArgumentPrefix);
    }

    [Fact]
    public void Resolve_ExeOnPath_WinsOverCmdShim() {
        var spec = Resolve("uip", $@"{PathDir}\uip.exe", $@"{PathDir}\uip.cmd");

        Assert.NotNull(spec);
        Assert.Equal($@"{PathDir}\uip.exe", spec.FileName);
        Assert.Equal(string.Empty, spec.ArgumentPrefix);
    }

    [Fact]
    public void Resolve_CmdShim_IsPreferredOverPs1AndLaunchedViaCmd() {
        var spec = Resolve("uip", $@"{PathDir}\uip.cmd", $@"{PathDir}\uip.ps1");

        Assert.NotNull(spec);
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal($"/c \"\"{PathDir}\\uip.cmd\" ", spec.ArgumentPrefix);
        Assert.Equal("\"", spec.ArgumentSuffix);
        Assert.Equal($@"{PathDir}\uip.cmd", spec.ResolvedPath);
    }

    [Fact]
    public void Resolve_CmdShim_ComposedCommandLine_HasDoubledOuterQuotes() {
        // Verified live: cmd.exe /c ""<shim>" <args>"" works; single outer quotes fail.
        var spec = Resolve("uip", $@"{PathDir}\uip.cmd");

        Assert.NotNull(spec);
        var commandLine = spec.ArgumentPrefix + "rpa validate --project-dir \"C:\\projects\\testProcess\" --output json" + spec.ArgumentSuffix;

        Assert.Equal(
            $"/c \"\"{PathDir}\\uip.cmd\" rpa validate --project-dir \"C:\\projects\\testProcess\" --output json\"",
            commandLine);
    }

    [Fact]
    public void Resolve_OnlyPs1Shim_FallsBackToPowerShell() {
        var spec = Resolve("uip", $@"{PathDir}\uip.ps1");

        Assert.NotNull(spec);
        Assert.Equal("powershell.exe", spec.FileName);
        Assert.Equal($"-NoProfile -ExecutionPolicy Bypass -File \"{PathDir}\\uip.ps1\" ", spec.ArgumentPrefix);
        Assert.Equal(string.Empty, spec.ArgumentSuffix);
    }

    [Fact]
    public void Resolve_ExtensionlessShim_IsStartedDirectly() {
        var spec = Resolve("uip", $@"{PathDir}\uip");

        Assert.NotNull(spec);
        Assert.Equal($@"{PathDir}\uip", spec.FileName);
        Assert.Equal(string.Empty, spec.ArgumentPrefix);
    }

    [Fact]
    public void Resolve_ConfiguredExeMissing_FallsBackToOtherExtensionsOfSameBaseName() {
        // Legacy configs say "uip.exe", but npm only installs the .cmd shim.
        var spec = Resolve("uip.exe", $@"{PathDir}\uip.cmd");

        Assert.NotNull(spec);
        Assert.Equal("cmd.exe", spec.FileName);
        Assert.Equal($@"{PathDir}\uip.cmd", spec.ResolvedPath);
    }

    [Fact]
    public void Resolve_ConfiguredExePresent_KeepsConfiguredExtensionPriority() {
        var spec = Resolve("uip.exe", $@"{PathDir}\uip.exe", $@"{PathDir}\uip.cmd");

        Assert.NotNull(spec);
        Assert.Equal($@"{PathDir}\uip.exe", spec.FileName);
    }

    [Fact]
    public void Resolve_NothingFound_ReturnsNull() {
        var spec = Resolve("uip", $@"{PathDir}\unrelated.exe");

        Assert.Null(spec);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_EmptyConfiguredName_ReturnsNull(string configured) {
        Assert.Null(Resolve(configured));
    }
}
