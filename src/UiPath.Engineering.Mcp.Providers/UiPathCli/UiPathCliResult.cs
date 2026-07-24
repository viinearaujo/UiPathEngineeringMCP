namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public sealed class UiPathCliResult {
    public bool Success { get; init; }
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}