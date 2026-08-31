using System.Text.Json;
using ModelContextProtocol.Protocol;
using UiPath.Engineering.Mcp.Core;
using UiPath.Engineering.Mcp.Server;

namespace UiPath.Engineering.Mcp.Server.Tests;

public class McpToolCallLoggingTests {
    [Fact]
    public void Describe_JsonError_ReadsStatusAndErrorCode() {
        using var doc = JsonDocument.Parse(
            $$"""{"status":"error","errorDetails":[{"errorCode":"{{ToolErrorCodes.PathNotAllowed}}","message":"blocked","fixHint":"use an allowed root"}]}""");
        var result = new CallToolResult {
            IsError = true,
            StructuredContent = doc.RootElement.Clone()
        };

        var (status, errorCode) = McpToolCallLogging.Describe(result);

        Assert.Equal("error", status);
        Assert.Equal(ToolErrorCodes.PathNotAllowed, errorCode);
    }

    [Fact]
    public void Describe_JsonElement_DoesNotRequireFileBody() {
        using var doc = JsonDocument.Parse("""{"status":"success","summary":"ok"}""");
        var result = new CallToolResult {
            StructuredContent = doc.RootElement.Clone()
        };

        var (status, errorCode) = McpToolCallLogging.Describe(result);

        Assert.Equal("success", status);
        Assert.Null(errorCode);
    }

    [Fact]
    public void Describe_IsErrorWithoutStructuredContent_UsesErrorStatus() {
        var result = new CallToolResult { IsError = true };

        var (status, errorCode) = McpToolCallLogging.Describe(result);

        Assert.Equal("error", status);
        Assert.Null(errorCode);
    }
}
