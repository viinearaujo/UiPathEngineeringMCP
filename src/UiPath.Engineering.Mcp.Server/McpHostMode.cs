namespace UiPath.Engineering.Mcp.Server;

public static class McpHostMode {
    public enum Kind { Http, Stdio }

    public static Kind FromArgs(string[] args) {
        foreach (var arg in args) {
            if (string.Equals(arg, "--stdio", StringComparison.OrdinalIgnoreCase)) {
                return Kind.Stdio;
            }
        }

        return Kind.Http;
    }
}
