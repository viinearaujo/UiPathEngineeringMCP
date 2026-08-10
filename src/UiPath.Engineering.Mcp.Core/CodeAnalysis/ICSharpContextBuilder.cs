namespace UiPath.Engineering.Mcp.Core.CodeAnalysis;

public interface ICSharpContextBuilder {
    Task<CSharpAnalysisContext> BuildAsync(string projectPath, CancellationToken cancellationToken = default);
}
