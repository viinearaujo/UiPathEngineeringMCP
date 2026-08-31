namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public interface IUiPathCliProvider {
    Task<UiPathCliResult> ValidateAsync(string projectPath, bool validate, bool build, bool pack, CancellationToken cancellationToken = default);

    // Runs an arbitrary CLI invocation, e.g. RunAsync("rpa", "init --name \"X\" ...").
    // 'verb' is used for output parsing/diagnostics; 'arguments' is split into
    // ArgumentList tokens (quoted segments stay one token).
    Task<UiPathCliResult> RunAsync(string verb, string arguments, string? workingDirectory = null, CancellationToken cancellationToken = default);
}
