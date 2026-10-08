using System.Data;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SqliteMigratedTemplateTests
{
    [Fact]
    public async Task ConcurrentCopiesMigrateOnceAndIsolateRowsSchemaAndSequences()
    {
        var migrations = 0;
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            Interlocked.Increment(ref migrations);
            await db.Database.MigrateAsync(ct);
        });
        var pending = Enumerable.Range(0, 3).Select(_ => Task.Run(() => owner.CreateDatabaseAsync())).ToArray();
        try
        {
            var leases = await Task.WhenAll(pending);
            Assert.Equal(1, migrations);
            Assert.Equal(3, leases.Select(lease => lease.Connection).Distinct().Count());
            await using var first = Context(leases[0].Connection);
            await using var second = Context(leases[1].Connection);
            await using var third = Context(leases[2].Connection);
            var firstClient = new Client { Name = "first SQLite clone" };
            first.Clients.Add(firstClient);
            await first.SaveChangesAsync();
            await first.Database.ExecuteSqlRawAsync("CREATE TABLE \"OwnedCloneOnly\" (\"Id\" integer NOT NULL PRIMARY KEY)");
            var secondClient = new Client { Name = "second SQLite clone" };
            second.Clients.Add(secondClient);
            await second.SaveChangesAsync();
            Assert.Equal(firstClient.Id, secondClient.Id);
            Assert.Equal("first SQLite clone", (await first.Clients.SingleAsync()).Name);
            Assert.Equal("second SQLite clone", (await second.Clients.SingleAsync()).Name);
            Assert.Empty(await third.Clients.ToArrayAsync());
            Assert.Equal("0", await ScalarAsync(leases[1].Connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='OwnedCloneOnly'"));
            Assert.Equal("0", await ScalarAsync(leases[2].Connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='OwnedCloneOnly'"));
            await using var later = await owner.CreateDatabaseAsync();
            await using var laterDb = Context(later.Connection);
            Assert.Empty(await laterDb.Clients.ToArrayAsync());
            Assert.Empty(await laterDb.Users.ToArrayAsync());
            Assert.Equal("0", await ScalarAsync(later.Connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name='OwnedCloneOnly'"));
            Assert.Equal(1, migrations);
        }
        finally
        {
            foreach (var task in pending)
                if (task.IsCompletedSuccessfully) await (await task).DisposeAsync();
        }
    }

    [Fact]
    public async Task CopyMatchesFreshMigratedSchemaHistoryPragmasAndIntegrity()
    {
        await using var fresh = new SqliteConnection("Data Source=:memory:");
        await fresh.OpenAsync();
        await using var baseline = Context(fresh);
        await baseline.Database.MigrateAsync();
        var email = await baseline.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled");
        if (email is null) baseline.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
        else email.Value = "false";
        await baseline.SaveChangesAsync();
        await using var owner = new SqliteMigratedTemplate();
        await using var lease = await owner.CreateDatabaseAsync();
        await using var clone = Context(lease.Connection);
        Assert.Equal(await CatalogAsync(fresh), await CatalogAsync(lease.Connection));
        Assert.Equal(await baseline.Database.GetAppliedMigrationsAsync(), await clone.Database.GetAppliedMigrationsAsync());
        Assert.Equal(clone.Database.GetMigrations(), await clone.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await clone.Database.GetPendingMigrationsAsync());
        Assert.False(clone.Database.HasPendingModelChanges());
        Assert.Equal("false", await clone.SystemSettings.Where(row => row.Key == "notifications.email.enabled").Select(row => row.Value).SingleAsync());
        foreach (var name in new[] { "foreign_keys", "recursive_triggers", "defer_foreign_keys", "journal_mode", "query_only" })
            Assert.Equal(await ScalarAsync(fresh, $"PRAGMA {name}"), await ScalarAsync(lease.Connection, $"PRAGMA {name}"));
        Assert.Equal("0", await ScalarAsync(lease.Connection, "SELECT COUNT(*) FROM \"__EFMigrationsLock\""));
        Assert.Equal("ok", await ScalarAsync(lease.Connection, "PRAGMA integrity_check"));
        Assert.Empty(await RowsAsync(lease.Connection, "PRAGMA foreign_key_check"));
        Assert.Equal(await RowsAsync(fresh, "SELECT name || ':' || seq FROM sqlite_sequence ORDER BY name"),
            await RowsAsync(lease.Connection, "SELECT name || ':' || seq FROM sqlite_sequence ORDER BY name"));
    }

    [Fact]
    public async Task SourceRejectsWritesWhileCopySupportsTransactionsAndObserverConnections()
    {
        SqliteConnection? source = null;
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            source = (SqliteConnection)db.Database.GetDbConnection();
            await db.Database.MigrateAsync(ct);
        });
        await using var lease = await owner.CreateDatabaseAsync();
        Assert.NotNull(source);
        Assert.Equal("1", await ScalarAsync(source, "PRAGMA query_only"));
        var error = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(source, "CREATE TABLE ForbiddenSourceWrite (Id integer)"));
        Assert.Equal(8, error.SqliteErrorCode);
        await using var db = Context(lease.Connection);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.Clients.Add(new Client { Name = "rolled back clone row" });
            await db.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Clients.ToArrayAsync());
        db.Clients.Add(new Client { Name = "committed clone row" });
        await db.SaveChangesAsync();
        await using var observer = Context(lease.Connection);
        Assert.Equal("committed clone row", (await observer.Clients.SingleAsync()).Name);
        await using var independent = await owner.CreateDatabaseAsync();
        await using var independentDb = Context(independent.Connection);
        Assert.Empty(await independentDb.Clients.ToArrayAsync());
    }

    [Fact]
    public async Task FileCopiesMatchFreshMigrationsAndKeepIndependentConnectionsAndJournaling()
    {
        using var root = new OwnedFileRoot();
        await using var fresh = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root.Path, "fresh.sqlite"), Pooling = false }.ConnectionString);
        await fresh.OpenAsync();
        await using var baseline = Context(fresh);
        await baseline.Database.MigrateAsync();
        await using var owner = new SqliteMigratedTemplate();
        var firstPath = Path.Combine(root.Path, "first.sqlite");
        await using var firstLease = await owner.CreateFileDatabaseAsync(firstPath);
        await using var secondLease = await owner.CreateFileDatabaseAsync(Path.Combine(root.Path, "second.sqlite"));
        Assert.True(File.Exists(firstPath));
        await using var first = Context(firstLease.Connection);
        await using var second = Context(secondLease.Connection);
        Assert.Equal(await CatalogAsync(fresh), await CatalogAsync(firstLease.Connection));
        Assert.Equal(await baseline.Database.GetAppliedMigrationsAsync(), await first.Database.GetAppliedMigrationsAsync());
        Assert.False(first.Database.HasPendingModelChanges());
        Assert.Empty(await first.Database.GetPendingMigrationsAsync());
        foreach (var name in new[] { "foreign_keys", "recursive_triggers", "defer_foreign_keys", "journal_mode", "synchronous", "query_only" })
            Assert.Equal(await ScalarAsync(fresh, $"PRAGMA {name}"), await ScalarAsync(firstLease.Connection, $"PRAGMA {name}"));
        Assert.Equal("delete", await ScalarAsync(firstLease.Connection, "PRAGMA journal_mode"));
        Assert.Equal("false", await first.SystemSettings.Where(row => row.Key == "notifications.email.enabled").Select(row => row.Value).SingleAsync());
        await using var observerConnection = new SqliteConnection(firstLease.Connection.ConnectionString);
        await observerConnection.OpenAsync();
        await using var observer = Context(observerConnection);
        await using (var transaction = await first.Database.BeginTransactionAsync())
        {
            first.Clients.Add(new Client { Name = "committed file clone row" });
            await first.SaveChangesAsync();
            Assert.Empty(await observer.Clients.ToArrayAsync());
            await transaction.CommitAsync();
        }
        Assert.Equal("committed file clone row", (await observer.Clients.SingleAsync()).Name);
        Assert.Empty(await second.Clients.ToArrayAsync());
        Assert.Empty(await baseline.Clients.ToArrayAsync());
        Assert.Equal("ok", await ScalarAsync(firstLease.Connection, "PRAGMA integrity_check"));
        Assert.Empty(await RowsAsync(firstLease.Connection, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task FileCopyRejectsRelativeAndExistingPathsBeforeMigrationOrOverwrite()
    {
        using var root = new OwnedFileRoot();
        var migrations = 0;
        await using var owner = new SqliteMigratedTemplate((_, _) =>
        {
            migrations++;
            throw new InvalidOperationException("Unexpected migration.");
        });
        await Assert.ThrowsAsync<ArgumentException>(() => owner.CreateFileDatabaseAsync("relative.sqlite"));
        var path = Path.Combine(root.Path, "existing.sqlite");
        await File.WriteAllBytesAsync(path, [9, 7, 3]);
        await Assert.ThrowsAsync<IOException>(() => owner.CreateFileDatabaseAsync(path));
        await Assert.ThrowsAsync<IOException>(() => owner.CreateFileDatabaseAsync(root.Path));
        Assert.Equal(new byte[] { 9, 7, 3 }, await File.ReadAllBytesAsync(path));
        Assert.Equal(0, migrations);
    }

    [Fact]
    public async Task FailedInitializationClosesPartialSourceAndCannotBeReused()
    {
        var marker = new InvalidOperationException("Owned SQLite initialization failure.");
        SqliteConnection? source = null;
        var calls = 0;
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            calls++;
            source = (SqliteConnection)db.Database.GetDbConnection();
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE PartialOwnedSchema (Id integer)", ct);
            throw marker;
        });
        Assert.Same(marker, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync()));
        Assert.NotNull(source);
        Assert.Equal(ConnectionState.Closed, source.State);
        Assert.Same(marker, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelledInitializationClosesPartialSourceAndCannotBeReused()
    {
        using var cancellation = new CancellationTokenSource();
        SqliteConnection? source = null;
        var calls = 0;
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            calls++;
            source = (SqliteConnection)db.Database.GetDbConnection();
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE PartialOwnedSchema (Id integer)", ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotNull(source);
        Assert.Equal(ConnectionState.Closed, source.State);
        Assert.Same(error, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task IncompleteMigrationNeverPublishesReadySource()
    {
        SqliteConnection? source = null;
        await using var owner = new SqliteMigratedTemplate((db, _) =>
        {
            source = (SqliteConnection)db.Database.GetDbConnection();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<Exception>(() => owner.CreateDatabaseAsync());
        Assert.NotNull(source);
        Assert.Equal(ConnectionState.Closed, source.State);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedOrCancelledCopyClosesDestinationAndPreservesReadySource(bool cancel, bool file)
    {
        using var root = new OwnedFileRoot();
        using var cancellation = new CancellationTokenSource();
        var marker = new InvalidOperationException("Owned SQLite copy failure.");
        SqliteConnection? failedDestination = null;
        var migrations = 0;
        var copies = 0;
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            migrations++;
            await db.Database.MigrateAsync(ct);
        }, (source, destination) =>
        {
            source.BackupDatabase(destination);
            if (++copies != 1) return;
            failedDestination = destination;
            if (cancel) cancellation.Cancel();
            else throw marker;
        });
        Task<SqliteMigratedTemplate.DatabaseLease> Copy(string name, CancellationToken ct = default)
            => file ? owner.CreateFileDatabaseAsync(Path.Combine(root.Path, name + ".sqlite"), ct) : owner.CreateDatabaseAsync(ct);
        if (cancel)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Copy("failed", cancellation.Token));
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        else Assert.Same(marker, await Assert.ThrowsAsync<InvalidOperationException>(() => Copy("failed")));
        Assert.NotNull(failedDestination);
        Assert.Equal(ConnectionState.Closed, failedDestination.State);
        await using var recovered = await Copy("recovered");
        await using var db = Context(recovered.Connection);
        Assert.Empty(await db.Users.ToArrayAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, migrations);
        Assert.Equal(2, copies);
    }

    [Fact]
    public async Task CancellingWaitingCopyDoesNotCancelInitializationOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new SqliteMigratedTemplate(async (db, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            await db.Database.MigrateAsync(ct);
        });
        var pending = owner.CreateDatabaseAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            var waiting = owner.CreateDatabaseAsync(cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            release.TrySetResult();
            await using var first = await pending;
        }
        await using var later = await owner.CreateDatabaseAsync();
        await using var db = Context(later.Connection);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task OwnerDisposalRequiresReturnedCopiesAndClosesOnlyItsOwnSource()
    {
        SqliteConnection? source = null;
        await using var first = new SqliteMigratedTemplate(async (db, ct) =>
        {
            source = (SqliteConnection)db.Database.GetDbConnection();
            await db.Database.MigrateAsync(ct);
        });
        await using var other = new SqliteMigratedTemplate();
        await using var firstLease = await first.CreateDatabaseAsync();
        await using var otherLease = await other.CreateDatabaseAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.DisposeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.CreateDatabaseAsync());
        Assert.NotNull(source);
        Assert.Equal(ConnectionState.Open, source.State);
        await firstLease.DisposeAsync();
        Assert.Equal(ConnectionState.Closed, firstLease.Connection.State);
        await first.DisposeAsync();
        Assert.Equal(ConnectionState.Closed, source.State);
        Assert.Equal(ConnectionState.Open, otherLease.Connection.State);
        Assert.Equal("ok", await ScalarAsync(otherLease.Connection, "PRAGMA integrity_check"));
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotInitializeSource()
    {
        var calls = 0;
        await using var owner = new SqliteMigratedTemplate((_, _) =>
        {
            calls++;
            throw new InvalidOperationException("Unexpected migration.");
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync(cancellation.Token));
        Assert.Equal(0, calls);
    }

    private static PaymentContext Context(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);

    private sealed class OwnedFileRoot : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pp_sqlite_template_" + Guid.NewGuid().ToString("N"));

        public OwnedFileRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            Assert.Equal(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()).TrimEnd(System.IO.Path.DirectorySeparatorChar),
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)));
            Assert.StartsWith("pp_sqlite_template_", System.IO.Path.GetFileName(Path), StringComparison.Ordinal);
            Directory.Delete(Path, recursive: true);
        }
    }

    private static Task<string[]> CatalogAsync(SqliteConnection connection)
        => RowsAsync(connection, "SELECT type || ':' || name || ':' || tbl_name || ':' || coalesce(sql,'') FROM sqlite_schema ORDER BY type,name");

    private static async Task<string[]> RowsAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }

    private static async Task<string> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
