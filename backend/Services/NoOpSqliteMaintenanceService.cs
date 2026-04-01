namespace PayPlanner.Api.Services;

public sealed class NoOpSqliteMaintenanceService : ISqliteMaintenanceService
{
    private readonly ILogger<NoOpSqliteMaintenanceService> _logger;

    public NoOpSqliteMaintenanceService(ILogger<NoOpSqliteMaintenanceService> logger)
    {
        _logger = logger;
    }

    public Task<SqliteStorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Empty());

    public Task<SqliteStorageStats> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("SQLite checkpoint skipped: current database provider does not use SQLite.");
        return Task.FromResult(Empty());
    }

    public Task<(SqliteStorageStats Before, SqliteStorageStats After)> CompactAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("SQLite compaction skipped: current database provider does not use SQLite.");
        var stats = Empty();
        return Task.FromResult((stats, stats));
    }

    private static SqliteStorageStats Empty()
        => new(
            DatabaseFileSizeBytes: 0,
            WalFileSizeBytes: 0,
            ShmFileSizeBytes: 0,
            TotalSizeBytes: 0,
            EstimatedReclaimableBytes: 0);
}
