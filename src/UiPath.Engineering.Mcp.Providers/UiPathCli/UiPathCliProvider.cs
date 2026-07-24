using System.Diagnostics;
using Microsoft.Extensions.Options;
using UiPath.Engineering.Mcp.Core.Configuration;

namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public sealed class UiPathCliProvider : IUiPathCliProvider {
    private readonly UiPathCliOptions _options;
    public UiPathCliProvider(IOptions<UiPathCliOptions> options) => _options = options.Value;

    public async Task<UiPathCliResult> ValidateAsync(string projectPath, bool restore, bool analyze, bool pack, CancellationToken cancellationToken = default) {
        var args = new List<string>();
        if (restore) args.Add("restore");
        if (analyze) args.Add("analyze");
        if (pack) args.Add("pack");
        
        args.Add($"\"{projectPath}\"");
        var command = string.Join(" ", args);

        var psi = new ProcessStartInfo {
            FileName = _options.ExecutablePath,
            Arguments = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return new UiPathCliResult { Success = false, Summary = "Failed to start uip.exe", Errors = ["Process start returned null."] };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(_options.DefaultTimeoutSeconds));

        try {
            await process.WaitForExitAsync(cts.Token);
            var stdOut = await process.StandardOutput.ReadToEndAsync();
            var stdErr = await process.StandardError.ReadToEndAsync();

            var errors = new List<string>();
            var warnings = new List<string>();
            
            // Simple parsing logic to avoid returning raw console output
            if (!string.IsNullOrWhiteSpace(stdErr)) errors.Add(stdErr.Trim());
            if (stdOut.Contains("error", StringComparison.OrdinalIgnoreCase)) errors.Add("CLI reported errors in output.");
            if (stdOut.Contains("warning", StringComparison.OrdinalIgnoreCase)) warnings.Add("CLI reported warnings in output.");

            return new UiPathCliResult {
                Success = process.ExitCode == 0,
                Command = $"{_options.ExecutablePath} {command}",
                ExitCode = process.ExitCode,
                Summary = process.ExitCode == 0 ? "Validation completed." : "Validation failed.",
                Errors = errors,
                Warnings = warnings
            };
        } catch (OperationCanceledException) {
            process.Kill();
            return new UiPathCliResult { Success = false, Summary = "CLI execution timed out.", Errors = ["Timeout exceeded."] };
        }
    }
}