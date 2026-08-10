namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public interface ICSharpAnalysisService {
    Task<FindSymbolResult> FindSymbolAsync(string projectPath, string symbol, string? kind = null, CancellationToken cancellationToken = default);
    Task<FindReferencesResult> FindReferencesAsync(string projectPath, string symbol, CancellationToken cancellationToken = default);
    Task<CodeContextResult> GetCodeContextAsync(string projectPath, string? symbol = null, string? file = null, int? line = null, CancellationToken cancellationToken = default);
    Task<CompileDiagnosticsResult> GetDiagnosticsAsync(string projectPath, string? severity = null, CancellationToken cancellationToken = default);
}
