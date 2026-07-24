namespace UiPath.Engineering.Mcp.Providers.UiPathCli;
public interface IUiPathCliProvider {
    Task<UiPathCliResult> ValidateAsync(string projectPath, bool restore, bool analyze, bool pack, CancellationToken cancellationToken = default);
}