namespace UiPath.Engineering.Mcp.Core;

/// <summary>
/// Shared cap for GetFileSize-before-ReadAllText on read, search, and resource paths.
/// Numeric value matches the existing docs/code-search guard (bytes vs characters
/// treated as the same ceiling so oversized files are never loaded).
/// </summary>
public static class FileReadLimits {
    public const int MaxFileBytes = 2_000_000;

    public static string OversizedMessage(string? name, long byteLength) {
        var label = string.IsNullOrWhiteSpace(name) ? "File" : $"'{name}'";
        return $"{label} is too large to read ({byteLength} bytes; max {MaxFileBytes}).";
    }
}
