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
}
