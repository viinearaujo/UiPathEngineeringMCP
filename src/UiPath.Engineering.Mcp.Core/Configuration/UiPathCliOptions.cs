namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class UiPathCliOptions {
    public string ExecutablePath { get; init; } = "uip.exe";
    public int DefaultTimeoutSeconds { get; init; } = 300;
    public bool IncludeRawOutput { get; init; }
    public string DefaultPackOutputDirectory { get; init; } = string.Empty;
}