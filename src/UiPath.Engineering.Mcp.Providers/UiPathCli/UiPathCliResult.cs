namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public sealed class UiPathCliResult {
    public bool Success { get; init; }
    public string Command { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public string Summary { get; init; } = string.Empty;
    public CliStepResult Validate { get; init; } = new();
    public CliStepResult Build { get; init; } = new();
    public CliStepResult Pack { get; init; } = new();
    public List<string> Errors { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> RawOutputLines { get; init; } = [];
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
}
