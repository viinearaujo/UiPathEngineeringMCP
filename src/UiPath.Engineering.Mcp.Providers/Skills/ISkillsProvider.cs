namespace UiPath.Engineering.Mcp.Providers.Skills;
public interface ISkillsProvider {
    Task<IReadOnlyList<SkillSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<SkillReadResult> ReadAsync(string name, string? file = null, CancellationToken cancellationToken = default);
}
