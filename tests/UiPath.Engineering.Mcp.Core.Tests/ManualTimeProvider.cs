namespace UiPath.Engineering.Mcp.Core.Tests;

internal sealed class ManualTimeProvider : TimeProvider {
    private DateTimeOffset _utcNow = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow += delta;
}
