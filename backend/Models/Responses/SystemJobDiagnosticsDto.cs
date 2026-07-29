namespace PayPlanner.Api.Models.Responses;

public sealed class SystemJobDiagnosticsDto
{
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<SystemJobDiagnosticsItemDto> Items { get; init; } = [];
}

public sealed class SystemJobDiagnosticsItemDto
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public string Status { get; init; } = "unknown";
    public string StatusText { get; init; } = string.Empty;
    public string IntervalText { get; init; } = string.Empty;
    public int? PendingCount { get; init; }
    public int? LastProcessedCount { get; init; }
    public DateTime? LastActivityAt { get; init; }
    public string? LastActivityDescription { get; init; }
    public string? NextRunHint { get; init; }
    public string? LastRunStatus { get; init; }
    public string? LastRunStatusText { get; init; }
    public DateTime? LastRunStartedAt { get; init; }
    public DateTime? LastRunFinishedAt { get; init; }
    public DateTime? LastSuccessAt { get; init; }
    public long? LastRunDurationMs { get; init; }
    public string? LastRunError { get; init; }
    public bool ManualRunEnabled { get; init; }
    public string? ManualRunHint { get; init; }
    public CourtSyncMetricsDto? CourtSyncMetrics { get; init; }
}

public sealed class CourtSyncMetricsDto
{
    public DateTime StartedAt { get; init; }
    public long RequestCount { get; init; }
    public long FailedRequestCount { get; init; }
    public double AverageLatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public long DuplicateSkipCount { get; init; }
    public long DocumentCount { get; init; }
    public long DocumentBytes { get; init; }
    public CourtSyncRetainedMetricsDto? Retained { get; init; }
}

public sealed class CourtSyncRetainedMetricsDto
{
    public int RetentionDays { get; init; }
    public string? PeriodStartedOn { get; init; }
    public string? PeriodEndedOn { get; init; }
    public long RequestCount { get; init; }
    public long FailedRequestCount { get; init; }
    public double AverageLatencyMs { get; init; }
    public double MaxLatencyMs { get; init; }
    public long DuplicateSkipCount { get; init; }
    public long DocumentCount { get; init; }
    public long DocumentBytes { get; init; }
}

public sealed class SystemJobManualRunResultDto
{
    public string JobKey { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public int? ProcessedCount { get; init; }
    public int? SkippedCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public SystemJobDiagnosticsDto Diagnostics { get; init; } = new();
}
