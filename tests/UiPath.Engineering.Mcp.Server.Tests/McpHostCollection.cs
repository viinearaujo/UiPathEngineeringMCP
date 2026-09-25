namespace UiPath.Engineering.Mcp.Server.Tests;

/// <summary>
/// Serializes WebApplicationFactory-based tests. Concurrent MCP host startups race inside the
/// SDK's tool-schema builder and can falsely advertise the bound <c>progress</c> parameter.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class McpHostCollection {
    public const string Name = "McpHost";
}
