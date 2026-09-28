using UiPath.Engineering.Mcp.Providers;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class ProcessRunnerTests {
    [Fact]
    public void CreateStartInfo_UsesArgumentList_NotConcatenatedArgumentsString() {
        var psi = ProcessRunner.CreateStartInfo(
            "git",
            ["-C", @"C:\foo & bar", "status", "--porcelain=v1", "--branch"],
            workingDirectory: null);

        Assert.Equal("git", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(5, psi.ArgumentList.Count);
        Assert.Equal("-C", psi.ArgumentList[0]);
        Assert.Equal(@"C:\foo & bar", psi.ArgumentList[1]);
        Assert.Equal("status", psi.ArgumentList[2]);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("-C \"", StringComparison.Ordinal));
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("/c", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CreateStartInfo_CmdShim_PassesHostSwitchAndShimAsSeparateTokens() {
        var psi = ProcessRunner.CreateStartInfo(
            "cmd.exe",
            ["/c", @"C:\npm\uip.cmd", "rpa", "validate", "--project-dir", @"C:\proj & calc"],
            workingDirectory: @"C:\work");

        Assert.Equal("cmd.exe", psi.FileName);
        Assert.False(psi.UseShellExecute);
        Assert.Equal("/c", psi.ArgumentList[0]);
        Assert.Equal(@"C:\npm\uip.cmd", psi.ArgumentList[1]);
        Assert.Equal(@"C:\proj & calc", psi.ArgumentList[^1]);
        Assert.Equal(@"C:\work", psi.WorkingDirectory);
        Assert.DoesNotContain(psi.ArgumentList, a => a.Contains("/c \"\"", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateStartInfo_PinsUtf8StreamEncoding() {
        var psi = ProcessRunner.CreateStartInfo("git", ["status"], null);

        Assert.Equal(System.Text.Encoding.UTF8.WebName, psi.StandardOutputEncoding?.WebName);
        Assert.Equal(System.Text.Encoding.UTF8.WebName, psi.StandardErrorEncoding?.WebName);
    }

    [Fact]
    public void SplitQuotedArguments_SplitsOnWhitespace_AndKeepsQuotedSegments() {
        var tokens = ProcessRunner.SplitQuotedArguments(
            "rpa validate --project-dir \"C:\\projects\\test Process\" --output json");

        Assert.Equal(
            ["rpa", "validate", "--project-dir", @"C:\projects\test Process", "--output", "json"],
            tokens);
    }

    [Fact]
    public void SplitQuotedArguments_MetacharactersInsideQuotes_StayOneToken() {
        var tokens = ProcessRunner.SplitQuotedArguments("--project-dir \"C:\\foo & bar\" --output json");

        Assert.Equal(["--project-dir", @"C:\foo & bar", "--output", "json"], tokens);
    }

    [Fact]
    public async Task RunAsync_ArgumentList_LaunchesWithoutShell() {
        var psi = ProcessRunner.CreateStartInfo("dotnet", ["--version"], null);
        Assert.Equal("dotnet", psi.FileName);
        Assert.Equal(["--version"], psi.ArgumentList.ToArray());
        Assert.False(psi.UseShellExecute);

        var run = await ProcessRunner.RunAsync(
            "dotnet", ["--version"], null, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Null(run.StartError);
        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(run.StdOut));
    }

    [Fact]
    public async Task RunAsync_Timeout_SetsTimedOut_NotCanceled() {
        var (fileName, arguments) = LongRunningCommand();
        var run = await ProcessRunner.RunAsync(
            fileName, arguments, null, TimeSpan.FromMilliseconds(400), CancellationToken.None);

        Assert.Null(run.StartError);
        Assert.True(run.TimedOut);
        Assert.False(run.Canceled);
        Assert.Equal(-1, run.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CallerCancel_SetsCanceled_NotTimedOut() {
        var (fileName, arguments) = LongRunningCommand();
        using var cts = new CancellationTokenSource();
        var runTask = ProcessRunner.RunAsync(
            fileName, arguments, null, TimeSpan.FromSeconds(60), cts.Token);

        await Task.Delay(200);
        cts.Cancel();
        var run = await runTask;

        Assert.Null(run.StartError);
        Assert.True(run.Canceled);
        Assert.False(run.TimedOut);
        Assert.Equal(-1, run.ExitCode);
    }

    [Fact]
    public async Task WaitForReadsAsync_CompletedTasks_Finishes() {
        using var cts = new CancellationTokenSource();
        var finished = await ProcessRunner.WaitForReadsAsync(Task.CompletedTask, Task.CompletedTask, cts);

        Assert.True(finished);
    }

    [Fact]
    public async Task RunAsync_CapsCapturedOutput() {
        var run = await ProcessRunner.RunAsync(
            "bash",
            ["-c", "printf '%2000s' ''"],
            null,
            TimeSpan.FromSeconds(10),
            CancellationToken.None,
            maxCapturedChars: 100);

        Assert.Equal(0, run.ExitCode);
        Assert.True(run.OutputTruncated);
        Assert.True(run.StdOut.Length <= 100);
        Assert.StartsWith(" ", run.StdOut);
    }

    [Fact]
    public async Task RunAsync_GrandchildHoldingStdout_ReturnsPartialOutput() {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) {
            Assert.Skip("Unix fork keeps the inherited stdout pipe open.");
        }

        const string script = """
            import os, sys, time
            pid = os.fork()
            if pid == 0:
                time.sleep(8)
                os._exit(0)
            sys.stdout.write("hello\n")
            sys.stdout.flush()
            os._exit(0)
            """;
        var run = await ProcessRunner.RunAsync(
            "python3",
            ["-c", script],
            null,
            TimeSpan.FromSeconds(8),
            CancellationToken.None);

        Assert.Null(run.StartError);
        Assert.False(run.TimedOut);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("hello", run.StdOut);
        Assert.True(run.OutputTruncated);
    }

    private static (string FileName, string[] Arguments) LongRunningCommand() =>
        OperatingSystem.IsWindows()
            ? ("powershell", ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"])
            : ("sleep", ["30"]);
}
