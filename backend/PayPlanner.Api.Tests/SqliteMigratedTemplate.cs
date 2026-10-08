using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>Class-owned migrated SQLite source with a separately owned copy per test.</summary>
public sealed class SqliteMigratedTemplate : IAsyncLifetime, IAsyncDisposable
{
    private static readonly string[] ConnectionPragmas = ["foreign_keys", "recursive_triggers", "defer_foreign_keys", "journal_mode"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<DatabaseLease> leases = [];
    private readonly Dictionary<string, string> pragmas = [];
    private readonly Func<PaymentContext, CancellationToken, Task> migrate;
    private readonly Action<SqliteConnection, SqliteConnection> copy;
    private SqliteConnection? source;
    private ExceptionDispatchInfo? initializationFailure;
    private bool ready;
    private bool closing;
    private bool disposed;

    public SqliteMigratedTemplate() : this((db, ct) => db.Database.MigrateAsync(ct)) { }

    internal SqliteMigratedTemplate(Func<PaymentContext, CancellationToken, Task> migrate,
        Action<SqliteConnection, SqliteConnection>? copy = null)
    {
        this.migrate = migrate;
        this.copy = copy ?? ((source, destination) => source.BackupDatabase(destination));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    internal Task<DatabaseLease> CreateDatabaseAsync(CancellationToken ct = default)
        => CreateDatabaseCoreAsync(null, ct);

    internal Task<DatabaseLease> CreateFileDatabaseAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("SQLite clone path must be absolute.", nameof(path));
        return CreateDatabaseCoreAsync(Path.GetFullPath(path), ct);
    }

    private async Task<DatabaseLease> CreateDatabaseCoreAsync(string? path, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
            initializationFailure?.Throw();
            if (path is not null && (File.Exists(path) || Directory.Exists(path)))
                throw new IOException("SQLite clone destination must not already exist.");
            await EnsureReadyAsync(ct);
            // The fixture owns the directory and file; the lease owns its connection.
            // CreateNew never overwrites an existing database, including after source preparation.
            if (path is not null)
                await using (var reservation = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path ?? ":memory:", Pooling = false, Mode = path is null ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite }.ConnectionString);
            try
            {
                await destination.OpenAsync(ct);
                var fileJournal = path is null ? null : await ScalarAsync(destination, "PRAGMA journal_mode", ct);
                if (fileJournal is not null) Assert.Equal("delete", fileJournal);
                ct.ThrowIfCancellationRequested();
                var started = Stopwatch.GetTimestamp();
                copy(source!, destination);
                // BackupDatabase is synchronous and has no cancellation overload.
                // Cancellation observed after copy must close the unissued destination.
                ct.ThrowIfCancellationRequested();
                foreach (var (name, value) in pragmas)
                {
                    // Keep normal file journaling for independent connections and crash tests.
                    var expected = name == "journal_mode" && fileJournal is not null ? fileJournal : value;
                    await ExecuteAsync(destination, $"PRAGMA {name}={expected}", ct);
                    Assert.Equal(expected, await ScalarAsync(destination, $"PRAGMA {name}", ct));
                }
                Assert.Equal("0", await ScalarAsync(destination, "PRAGMA query_only", ct));
                Assert.Equal("1", await ScalarAsync(source!, "PRAGMA query_only", ct));
                ct.ThrowIfCancellationRequested();
                var lease = new DatabaseLease(this, destination);
                leases.Add(lease);
                Report($"Owned SQLite migrated clone: copyMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
                return lease;
            }
            catch
            {
                await destination.DisposeAsync();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (ready) return;
        source = new SqliteConnection("Data Source=:memory:;Pooling=False");
        try
        {
            await source.OpenAsync(ct);
            var started = Stopwatch.GetTimestamp();
            int migrationCount;
            await using (var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite(source).UsePayPlannerSqliteMigrations().Options))
            {
                await migrate(db, ct);
                Assert.False(db.Database.HasPendingModelChanges());
                var expected = db.Database.GetMigrations().ToArray();
                Assert.NotEmpty(expected);
                Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
                migrationCount = expected.Length;
                Assert.Empty(await db.Users.ToArrayAsync(ct));
                var email = await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled", ct);
                if (email is null) db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                await db.SaveChangesAsync(ct);
                Assert.Null(db.Database.CurrentTransaction);
            }
            Assert.Equal("0", await ScalarAsync(source, "SELECT COUNT(*) FROM \"__EFMigrationsLock\"", ct));
            foreach (var name in ConnectionPragmas)
            {
                var value = await ScalarAsync(source, $"PRAGMA {name}", ct);
                Assert.True(name == "journal_mode" ? value is "memory" or "off" : value is "0" or "1");
                pragmas.Add(name, value);
            }
            await ExecuteAsync(source, "PRAGMA query_only=ON", ct);
            Assert.Equal("1", await ScalarAsync(source, "PRAGMA query_only", ct));
            ct.ThrowIfCancellationRequested();
            ready = true;
            Report($"Owned SQLite migrated template: migrations={migrationCount}; prepareMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
        catch (Exception error)
        {
            initializationFailure = ExceptionDispatchInfo.Capture(error);
            await source.DisposeAsync();
            source = null;
            pragmas.Clear();
            throw;
        }
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("SQLite template observation is missing.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void Report(string message)
    {
        try { Console.WriteLine(message); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { /* Diagnostic output must not lose a created connection's ownership. */ }
    }

    private async ValueTask ReleaseAsync(DatabaseLease lease)
    {
        await gate.WaitAsync();
        try
        {
            if (lease.Released) return;
            if (!leases.Contains(lease)) throw new InvalidOperationException("SQLite lease does not belong to this owner.");
            await lease.Connection.DisposeAsync();
            leases.Remove(lease);
            lease.Released = true;
        }
        finally { gate.Release(); }
    }

    public async Task DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            if (disposed) return;
            closing = true;
            if (leases.Count != 0) throw new InvalidOperationException($"Cannot dispose SQLite template while {leases.Count} owned database leases remain.");
            if (source is not null) await source.DisposeAsync();
            source = null;
            disposed = true;
        }
        finally { gate.Release(); }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    internal sealed class DatabaseLease(SqliteMigratedTemplate owner, SqliteConnection connection) : IAsyncDisposable
    {
        internal SqliteConnection Connection { get; } = connection;
        internal bool Released { get; set; }
        public ValueTask DisposeAsync() => owner.ReleaseAsync(this);
    }
}
