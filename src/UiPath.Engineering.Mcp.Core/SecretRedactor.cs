using System.Text.RegularExpressions;

namespace UiPath.Engineering.Mcp.Core;

// Masks values of keys that look like credentials before file content is
// returned to an MCP client. Keys stay visible so structure remains useful.
public static class SecretRedactor {
    private const string KeyPattern =
        @"password|passwd|secret|token|apikey|api_key|accesskey|access_key|connectionstring|connection_string|clientsecret|client_secret|privatekey|private_key";

    // JSON-style: "somePasswordKey": "value"
    private static readonly Regex JsonPattern = new(
        $@"(""[^""]*(?:{KeyPattern})[^""]*""\s*:\s*"")[^""]*("")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // key=value / key: value on a single line (unquoted key, value not already redacted)
    private static readonly Regex KeyValuePattern = new(
        $@"([^\s""'=:]*(?:{KeyPattern})[^\s""'=:]*\s*[=:]\s*)(?!\*)\S.*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static (string Text, int RedactedCount) Redact(string content) {
        var count = 0;
        var text = JsonPattern.Replace(content, m => {
            count++;
            return m.Groups[1].Value + "***REDACTED***" + m.Groups[2].Value;
        });
        text = KeyValuePattern.Replace(text, m => {
            count++;
            return m.Groups[1].Value + "***REDACTED***";
        });
        return (text, count);
    }
}
