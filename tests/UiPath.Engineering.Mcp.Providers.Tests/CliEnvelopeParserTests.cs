using UiPath.Engineering.Mcp.Providers.UiPathCli;

namespace UiPath.Engineering.Mcp.Providers.Tests;

public class CliEnvelopeParserTests {
    [Fact]
    public void TryParse_ResponseEnvelope_ReadsResultCodeAndMessage() {
        const string stdOut = """{"Result":"Success","Code":"ToolResult","Message":"ok","Data":{"message":"hello"}}""";

        Assert.True(CliEnvelopeParser.TryParse(stdOut, out var envelope));
        Assert.True(envelope.IsSuccess);
        Assert.Equal("ToolResult", envelope.Code);
        Assert.Equal("ok", envelope.Message);
        Assert.NotNull(envelope.Data);
    }

    [Fact]
    public void TryParse_BannerTextBeforePayload_StillParses() {
        // The CLI can print update/banner text on stdout ahead of the JSON payload.
        const string stdOut = """
            Checking for updates.
            Update succeeded: @uipath/cli 1.200.1 -> 1.202.1; 54 skill(s) refreshed.
            {"Result":"Success","Code":"ToolResult","Data":{"message":"Found 1 enabled rule(s):"}}
            """;

        Assert.True(CliEnvelopeParser.TryParse(stdOut, out var envelope));
        Assert.True(envelope.IsSuccess);
        Assert.Equal("Found 1 enabled rule(s):", CliEnvelopeParser.GetString(envelope.Data!.Value, "message"));
    }

    [Fact]
    public void TryParse_FailureResult_IsNotSuccess() {
        const string stdOut = """{"Result":"ValidationError","Message":"The project is invalid."}""";

        Assert.True(CliEnvelopeParser.TryParse(stdOut, out var envelope));
        Assert.False(envelope.IsSuccess);
        Assert.Equal("ValidationError", envelope.Result);
    }

    [Fact]
    public void TryParse_NonJson_ReturnsFalse() {
        Assert.False(CliEnvelopeParser.TryParse("not json at all", out var envelope));
        Assert.Null(envelope.Data);
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalse() {
        Assert.False(CliEnvelopeParser.TryParse(null, out _));
        Assert.False(CliEnvelopeParser.TryParse("   ", out _));
    }

    [Fact]
    public void TryParse_JsonArrayRoot_ReturnsFalse() {
        Assert.False(CliEnvelopeParser.TryParse("""[{"Result":"Success"}]""", out _));
    }

    [Fact]
    public void Data_StaysReadable_AfterParseReturns() {
        const string stdOut = """{"Result":"Success","Data":{"message":"detached"}}""";

        CliEnvelopeParser.TryParse(stdOut, out var envelope);

        // The element is a detached clone, so it must not throw once the document is disposed.
        Assert.Equal("detached", CliEnvelopeParser.GetString(envelope.Data!.Value, "message"));
    }

    [Fact]
    public void GetBool_ReadsBooleanAndStringForms() {
        const string stdOut = """{"Data":{"hasErrors":true,"other":"false"}}""";
        CliEnvelopeParser.TryParse(stdOut, out var envelope);
        var data = envelope.Data!.Value;

        Assert.True(CliEnvelopeParser.GetBool(data, "hasErrors"));
        Assert.False(CliEnvelopeParser.GetBool(data, "other"));
        Assert.Null(CliEnvelopeParser.GetBool(data, "missing"));
    }

    [Fact]
    public void TryGetProperty_IsCaseInsensitive() {
        const string stdOut = """{"Data":{"DebugState":"Paused"}}""";
        CliEnvelopeParser.TryParse(stdOut, out var envelope);

        Assert.True(CliEnvelopeParser.TryGetProperty(envelope.Data!.Value, "debugState", out var value));
        Assert.Equal("Paused", value.GetString());
    }
}

