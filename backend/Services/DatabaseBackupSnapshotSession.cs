using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace PayPlanner.Api.Services;

/// <summary>
/// Owns the read-only database view used by both the dump and its file-reference inventory.
/// An exported PostgreSQL snapshot is usable only while this session remains open.
/// SQLite callers must supply the fresh VACUUM INTO copy, never the live database.
/// </summary>
public sealed class DatabaseBackupSnapshotSession : IAsyncDisposable
{
    private readonly DbConnection connection;
    private readonly DbTransaction transaction;
    private bool disposed;

    public string? PostgreSqlSnapshotId { get; }

    private DatabaseBackupSnapshotSession(DbConnection connection, DbTransaction transaction, string? snapshotId)
    {
        this.connection = connection;
        this.transaction = transaction;
        PostgreSqlSnapshotId = snapshotId;
    }

    public DbCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    public static async Task<DatabaseBackupSnapshotSession> OpenPostgreSqlAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = $"payplanner-snapshot-{Guid.NewGuid():N}",
        };
        if (string.IsNullOrWhiteSpace(settings.Database))
            throw new ArgumentException("Snapshot database must be explicit.", nameof(connectionString));
        var connection = new NpgsqlConnection(settings.ConnectionString);
        NpgsqlTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT pg_export_snapshot()";
            var snapshotId = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(snapshotId))
                throw new InvalidDataException("PostgreSQL did not export a snapshot.");
            return new DatabaseBackupSnapshotSession(connection, transaction, snapshotId);
        }
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
            throw;
        }
    }

    public static async Task<DatabaseBackupSnapshotSession> OpenSqliteCopyAsync(
        string copyPath, string liveDatabasePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(copyPath) || !Path.IsPathFullyQualified(liveDatabasePath))
            throw new ArgumentException("Snapshot paths must be fully qualified.");
        var copy = Path.GetFullPath(copyPath);
        var live = Path.GetFullPath(liveDatabasePath);
        if (string.Equals(copy, live, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("A live SQLite database cannot be used as its backup snapshot.");
        if ((File.GetAttributes(copy) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("A SQLite snapshot cannot be a symbolic link.");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = copy,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ConnectionString);
        DbTransaction? transaction = null;
        try
        {
            await connection.OpenAsync(cancellationToken);
            transaction = connection.BeginTransaction(deferred: true);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "SELECT name FROM sqlite_schema LIMIT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return new DatabaseBackupSnapshotSession(connection, transaction, null);
        }
        catch
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await connection.DisposeAsync(); }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try { await transaction.DisposeAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
