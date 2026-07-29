using UiPath.Engineering.Mcp.Core;

namespace UiPath.Engineering.Mcp.Core.Tests;

public class SecretRedactorTests {
    [Fact]
    public void Redact_JsonSecretValue_IsMasked() {
        var input = "{\n  \"LATAM_Password\": \"abc123\",\n  \"Proxy_Host\": \"mon-prod:9080\"\n}";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(1, count);
        Assert.Contains("\"LATAM_Password\": \"***REDACTED***\"", text);
        Assert.Contains("\"Proxy_Host\": \"mon-prod:9080\"", text);
    }

    [Fact]
    public void Redact_KeyEqualsValue_IsMasked() {
        var input = "DbPassword=secret1\nRegion=us-east-1";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(1, count);
        Assert.Contains("DbPassword=***REDACTED***", text);
        Assert.Contains("Region=us-east-1", text);
    }

    [Fact]
    public void Redact_NoSecrets_ReturnsInputUnchanged() {
        var input = "region=us-east-1\nhost=mon-prod-sqdrpavip-01";

        var (text, count) = SecretRedactor.Redact(input);

        Assert.Equal(0, count);
        Assert.Equal(input, text);
    }

    [Fact]
    public void Redact_MultipleSecrets_CountsEach() {
        var input = "ApiKey=aaa\nClientSecret=bbb";

        var (_, count) = SecretRedactor.Redact(input);

        Assert.Equal(2, count);
    }
}
