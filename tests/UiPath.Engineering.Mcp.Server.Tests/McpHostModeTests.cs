using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class McpHostModeTests {
    [Fact]
    public void FromArgs_NoArgs_IsHttp() {
        Assert.Equal(McpHostMode.Kind.Http, McpHostMode.FromArgs([]));
    }

    [Fact]
    public void FromArgs_StdioFlag_IsStdio() {
        Assert.Equal(McpHostMode.Kind.Stdio, McpHostMode.FromArgs(["--stdio"]));
    }

    [Fact]
    public void FromArgs_StdioAmongOtherArgs_IsStdio() {
        Assert.Equal(McpHostMode.Kind.Stdio, McpHostMode.FromArgs(["--urls", "http://localhost:5000", "--STDIO"]));
    }
}
