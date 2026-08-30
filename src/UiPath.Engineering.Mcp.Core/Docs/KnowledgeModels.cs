namespace UiPath.Engineering.Mcp.Core.Docs;

public sealed class KnowledgeArticle {
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> RelatedFiles { get; init; } = [];
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = Current;
    public string FileName { get; set; } = string.Empty;

    public const string Current = "current";
    public const string Deprecated = "deprecated";
}

public sealed class KnowledgeIndex {
    public List<KnowledgeArticle> Articles { get; init; } = [];
}
