using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseConnectionOperationTests
{
    [Fact]
    public async Task RepeatedQueriesUseOneOpeningAndReleaseOwnedConnection()
    {
        var observer = new ConnectionObserver();
        await using var db = CreateContext("Data Source=:memory:;Pooling=False", observer);
        var result = await DatabaseConnectionOperation.RunAsync(db, async () =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE Marker (Value INTEGER NOT NULL)");
            await db.Database.ExecuteSqlRawAsync("INSERT INTO Marker VALUES (1)");
            return await db.Database.SqlQueryRaw<int>("SELECT Value FROM Marker").SingleAsync();
        }, CancellationToken.None);
        Assert.Equal(1, result);
        Assert.Equal(1, observer.Openings);
        Assert.Equal(1, observer.Closings);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }

    [Fact]
    public async Task CallerOwnedConnectionRemainsOpen()
    {
        var observer = new ConnectionObserver();
        await using var db = CreateContext("Data Source=:memory:;Pooling=False", observer);
        await db.Database.OpenConnectionAsync();
        await DatabaseConnectionOperation.RunAsync(db, async () =>
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1");
            return true;
        }, CancellationToken.None);
        Assert.Equal(ConnectionState.Open, db.Database.GetDbConnection().State);
        Assert.Equal(1, observer.Openings);
        Assert.Equal(0, observer.Closings);
        await db.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task LaterReadSeesCommittedRevocationFromAnotherConnection()
    {
        var connectionString = $"Data Source=pp_connection_operation_{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        await using var reader = CreateContext(connectionString);
        await using var writer = CreateContext(connectionString);
        await writer.Database.ExecuteSqlRawAsync("CREATE TABLE Permission (Value INTEGER NOT NULL)");
        await writer.Database.ExecuteSqlRawAsync("INSERT INTO Permission VALUES (1)");
        await DatabaseConnectionOperation.RunAsync(reader, async () =>
        {
            Assert.Equal(1, await reader.Database.SqlQueryRaw<int>("SELECT Value FROM Permission").SingleAsync());
            await writer.Database.ExecuteSqlRawAsync("UPDATE Permission SET Value = 0");
            Assert.Equal(0, await reader.Database.SqlQueryRaw<int>("SELECT Value FROM Permission").SingleAsync());
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CleanupFailureDoesNotReplacePrimaryFailure()
    {
        var observer = new ConnectionObserver { FailClose = true };
        await using var db = CreateContext("Data Source=:memory:;Pooling=False", observer);
        var primary = new OperationCanceledException("Owned operation cancelled.");
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DatabaseConnectionOperation.RunAsync<int>(db, () => Task.FromException<int>(primary), CancellationToken.None));
        Assert.Same(primary, actual);
        Assert.Same(observer.CloseFailure, actual.Data["DatabaseConnectionOperation.CloseFailure"]);
        observer.FailClose = false;
        await db.Database.CloseConnectionAsync();
    }

    [Fact]
    public async Task CleanupFailureAfterSuccessIsReported()
    {
        var observer = new ConnectionObserver { FailClose = true };
        await using var db = CreateContext("Data Source=:memory:;Pooling=False", observer);
        var actual = await Assert.ThrowsAsync<IOException>(() =>
            DatabaseConnectionOperation.RunAsync(db, () => Task.FromResult(1), CancellationToken.None));
        Assert.Same(observer.CloseFailure, actual);
        observer.FailClose = false;
        await db.Database.CloseConnectionAsync();
    }

    private static DbContext CreateContext(string connectionString, ConnectionObserver? observer = null)
    {
        var builder = new DbContextOptionsBuilder().UseSqlite(connectionString);
        if (observer is not null) builder.AddInterceptors(observer);
        return new DbContext(builder.Options);
    }

    private sealed class ConnectionObserver : DbConnectionInterceptor
    {
        public int Openings { get; private set; }
        public int Closings { get; private set; }
        public bool FailClose { get; set; }
        public IOException CloseFailure { get; } = new("Owned close failure.");

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Openings++;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            Closings++;
            if (FailClose) throw CloseFailure;
            return ValueTask.FromResult(result);
        }
    }
}
