namespace UiPath.Engineering.Mcp.Core.Jobs;

public static class BackgroundJobStates {
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

public sealed class BackgroundJob {
    public required string JobId { get; init; }
    public required string ToolName { get; init; }
    public string State { get; set; } = BackgroundJobStates.Queued;
    public string? Phase { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; set; }
}

public interface IBackgroundJobStore {
    BackgroundJob Create(string toolName);
    bool TryGet(string jobId, out BackgroundJob job);
    void SetPhase(string jobId, string phase);
    void MarkRunning(string jobId);
    void Complete(string jobId, object result);
    void Fail(string jobId, string error);
}
