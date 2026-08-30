namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class AdrRecord {
    public string Id { get; init; } = string.Empty;
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Status { get; set; } = Proposed;
    public List<string> RelatedFiles { get; init; } = [];
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? Supersedes { get; set; }

    public const string Proposed = "proposed";
    public const string Accepted = "accepted";
    public const string Superseded = "superseded";
    public const string Deprecated = "deprecated";
}

public sealed class AdrIndex {
    public List<AdrRecord> Adrs { get; init; } = [];
}
