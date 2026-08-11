namespace UiPath.Engineering.Mcp.Core.CodeSearch;

public interface ICodebaseSearchService {
    Task<TextSearchResult> SearchTextAsync(string projectPath, string query, CancellationToken cancellationToken = default);
    Task<SymbolSearchResult> SearchSymbolsAsync(string projectPath, string query, string? kind = null, CancellationToken cancellationToken = default);
}
