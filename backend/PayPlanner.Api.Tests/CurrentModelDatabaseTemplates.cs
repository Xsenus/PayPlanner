using Microsoft.EntityFrameworkCore;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>
/// Class-owned sources for tests of the current EF model. This deliberately preserves
/// EnsureCreated semantics; migration/upgrade tests keep their separate preparation path.
/// Every issued lease is writable and independently owned, never a shared transaction.
/// </summary>
public sealed class CurrentModelDatabaseTemplates : IAsyncLifetime, IAsyncDisposable
{
    internal PostgreSqlMigratedTemplate PostgreSql { get; } = new(
        async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); }, migratedSchema: false);
    internal SqliteMigratedTemplate Sqlite { get; } = new(
        async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); }, migratedSchema: false);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Dispose both owners even if one detects an outstanding lease.
        await Task.WhenAll(PostgreSql.DisposeAsync(), Sqlite.DisposeAsync());
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
}
