namespace UiPath.Engineering.Mcp.Core.Configuration;
public sealed class McpServerOptions {
    public string Name { get; init; } = "UiPath Engineering MCP";
    public string Version { get; init; } = "0.1.0";
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// HTTP Copilot surface. CopilotDefault (default) advertises ≤12 tools.
    /// All exposes every registered tool (Inspector). GitLab tools are never deleted.
    /// </summary>
    public string ToolSurface { get; set; } = CopilotConnectorTools.SurfaceCopilotDefault;

    public HttpAuthOptions HttpAuth { get; set; } = new();
}
