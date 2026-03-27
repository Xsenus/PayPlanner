namespace PayPlanner.Api.Models.Responses;

public class SqliteStorageStatsDto
{
    public long DatabaseFileSizeBytes { get; init; }
    public long WalFileSizeBytes { get; init; }
    public long ShmFileSizeBytes { get; init; }
    public long TotalSizeBytes { get; init; }
    public long EstimatedReclaimableBytes { get; init; }
}

public class UserActivityMaintenanceDto
{
    public bool IsEnabled { get; init; }
    public int? RetentionDays { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int LogEntries { get; init; }
    public DateTime? OldestLogAt { get; init; }
    public DateTime? NewestLogAt { get; init; }
    public SqliteStorageStatsDto Storage { get; init; } = new();
}

public class UserActivityCleanupResultDto
{
    public int DeletedLogs { get; init; }
    public UserActivityMaintenanceDto Maintenance { get; init; } = new();
}

public class UserActivityCompactionResultDto
{
    public SqliteStorageStatsDto Before { get; init; } = new();
    public SqliteStorageStatsDto After { get; init; } = new();
    public UserActivityMaintenanceDto Maintenance { get; init; } = new();
}
