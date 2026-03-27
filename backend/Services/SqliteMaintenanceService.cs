using Microsoft.Data.Sqlite;

namespace PayPlanner.Api.Services;

public sealed record SqliteStorageStats(
    long DatabaseFileSizeBytes,
    long WalFileSizeBytes,
    long ShmFileSizeBytes,
    long TotalSizeBytes,
    long EstimatedReclaimableBytes);

public interface ISqliteMaintenanceService
{
    Task<SqliteStorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default);
    Task<SqliteStorageStats> CheckpointAsync(CancellationToken cancellationToken = default);
    Task<(SqliteStorageStats Before, SqliteStorageStats After)> CompactAsync(CancellationToken cancellationToken = default);
}

public class SqliteMaintenanceService : ISqliteMaintenanceService
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly ILogger<SqliteMaintenanceService> _logger;

    public SqliteMaintenanceService(IConfiguration configuration, ILogger<SqliteMaintenanceService> logger)
    {
        _logger = logger;

        var rawConnectionString = configuration.GetConnectionString("Default") ?? "Data Source=payplanner.db";
        var builder = new SqliteConnectionStringBuilder(rawConnectionString);
        var dataSource = builder.DataSource;

        _dbPath = Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, dataSource));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    public async Task<SqliteStorageStats> GetStorageStatsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await ReadStatsAsync(connection, cancellationToken);
    }

    public async Task<SqliteStorageStats> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        return await ReadStatsAsync(connection, cancellationToken);
    }

    public async Task<(SqliteStorageStats Before, SqliteStorageStats After)> CompactAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var before = await ReadStatsAsync(connection, cancellationToken);

        _logger.LogInformation("Starting SQLite compaction for {DatabasePath}", _dbPath);
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        await ExecuteNonQueryAsync(connection, "VACUUM;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken);
        var after = await ReadStatsAsync(connection, cancellationToken);
        _logger.LogInformation(
            "SQLite compaction finished. Before={BeforeBytes} bytes, After={AfterBytes} bytes",
            before.TotalSizeBytes,
            after.TotalSizeBytes);

        return (before, after);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout=30000;", cancellationToken);
        return connection;
    }

    private async Task<SqliteStorageStats> ReadStatsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pageSize = await ExecuteScalarLongAsync(connection, "PRAGMA page_size;", cancellationToken);
        var freelistCount = await ExecuteScalarLongAsync(connection, "PRAGMA freelist_count;", cancellationToken);

        var dbBytes = GetFileLengthSafe(_dbPath);
        var walBytes = GetFileLengthSafe($"{_dbPath}-wal");
        var shmBytes = GetFileLengthSafe($"{_dbPath}-shm");

        return new SqliteStorageStats(
            DatabaseFileSizeBytes: dbBytes,
            WalFileSizeBytes: walBytes,
            ShmFileSizeBytes: shmBytes,
            TotalSizeBytes: dbBytes + walBytes + shmBytes,
            EstimatedReclaimableBytes: Math.Max(0, freelistCount * pageSize));
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string commandText, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result switch
        {
            long value => value,
            int value => value,
            string value when long.TryParse(value, out var parsed) => parsed,
            _ => 0,
        };
    }

    private static long GetFileLengthSafe(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }
}
