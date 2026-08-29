using System.Text.Json;

namespace UiPath.Engineering.Mcp.Tools.Tests;

public class McpToolErrorMapperTests {
    [Theory]
    [InlineData("error", true)]
    [InlineData("Error", true)]
    [InlineData("success", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsErrorStatus_MatchesErrorCaseInsensitively(string? status, bool expected) {
        Assert.Equal(expected, McpToolErrorMapper.IsErrorStatus(status));
    }

    [Fact]
    public void StructuredContent_CamelCaseStatusError_IsError() {
        using var doc = JsonDocument.Parse("""{"status":"error","summary":"Path not allowed."}""");
        Assert.True(McpToolErrorMapper.StructuredContentIndicatesError(doc.RootElement));
    }

    [Fact]
    public void StructuredContent_PascalCaseStatusError_IsError() {
        using var doc = JsonDocument.Parse("""{"Status":"error","Summary":"broken"}""");
        Assert.True(McpToolErrorMapper.StructuredContentIndicatesError(doc.RootElement));
    }

    [Fact]
    public void StructuredContent_Success_IsNotError() {
        using var doc = JsonDocument.Parse("""{"status":"success","summary":"ok"}""");
        Assert.False(McpToolErrorMapper.StructuredContentIndicatesError(doc.RootElement));
    }

    [Fact]
    public void StructuredContent_NonObject_IsNotError() {
        using var doc = JsonDocument.Parse("""["x"]""");
        Assert.False(McpToolErrorMapper.StructuredContentIndicatesError(doc.RootElement));
    }

    [Fact]
    public void StructuredContent_NullObject_IsNotError() {
        Assert.False(McpToolErrorMapper.StructuredContentIndicatesError((object?)null));
    }
}
