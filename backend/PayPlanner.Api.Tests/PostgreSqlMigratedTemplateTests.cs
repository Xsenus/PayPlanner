using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PostgreSqlMigratedTemplateTests
{
    [Fact]
    public async Task ConcurrentClonesMigrateOnceAndIsolateRowsSchemaAndSequences()
    {
        var migrations = 0;
        await using var owner = new PostgreSqlMigratedTemplate(async (db, ct) =>
        {
            Interlocked.Increment(ref migrations);
            await db.Database.MigrateAsync(ct);
        });
        var pending = Enumerable.Range(0, 3).Select(_ => owner.CreateDatabaseAsync("pp_template_test_")).ToArray();
        try
        {
            var leases = await Task.WhenAll(pending);
            Assert.Equal(1, migrations);
            Assert.Equal(3, leases.Select(lease => lease.Name).Distinct().Count());
            await using var first = Context(leases[0]);
            await using var second = Context(leases[1]);
            await using var third = Context(leases[2]);
            foreach (var db in new[] { first, second, third })
            {
                Assert.Empty(await db.Clients.ToArrayAsync());
                Assert.Empty(await db.Users.ToArrayAsync());
                Assert.Equal("false", await db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
                Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
            }
            var firstClient = new Client { Name = "first clone" };
            first.Clients.Add(firstClient);
            await first.SaveChangesAsync();
            await first.Database.ExecuteSqlRawAsync("CREATE TABLE \"OwnedCloneOnly\" (\"Id\" integer NOT NULL PRIMARY KEY)");
            var secondClient = new Client { Name = "second clone" };
            second.Clients.Add(secondClient);
            await second.SaveChangesAsync();
            Assert.Equal(firstClient.Id, secondClient.Id);
            Assert.Equal("first clone", (await first.Clients.SingleAsync()).Name);
            Assert.Equal("second clone", (await second.Clients.SingleAsync()).Name);
            Assert.Empty(await third.Clients.ToArrayAsync());
            Assert.Null(await ScalarAsync(leases[1].ConnectionString, "SELECT to_regclass('public.\"OwnedCloneOnly\"')::text"));
            Assert.Null(await ScalarAsync(leases[2].ConnectionString, "SELECT to_regclass('public.\"OwnedCloneOnly\"')::text"));
            await using var later = await owner.CreateDatabaseAsync("pp_template_test_");
            await using var laterDb = Context(later);
            Assert.Empty(await laterDb.Clients.ToArrayAsync());
            Assert.Null(await ScalarAsync(later.ConnectionString, "SELECT to_regclass('public.\"OwnedCloneOnly\"')::text"));
            Assert.Equal(1, migrations);
        }
        finally
        {
            foreach (var task in pending)
                if (task.IsCompletedSuccessfully) await (await task).DisposeAsync();
        }
    }

    [Fact]
    public async Task ClonePreservesMigratedCatalogAndTemplateRejectsConnections()
    {
        string[]? migratedCatalog = null;
        await using var owner = new PostgreSqlMigratedTemplate(async (db, ct) =>
        {
            await db.Database.MigrateAsync(ct);
            migratedCatalog = await CatalogAsync(db.Database.GetConnectionString()!);
        });
        await using var lease = await owner.CreateDatabaseAsync("pp_template_test_");
        Assert.Equal(migratedCatalog, await CatalogAsync(lease.ConnectionString));
        await using var context = Context(lease);
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(1, await context.StorageCapacityEpochs.CountAsync());
        Assert.Equal(3, await context.GeneratedFinancialNumberScopes.CountAsync());
        var template = new NpgsqlConnectionStringBuilder(lease.ConnectionString) { Database = owner.TemplateName };
        await using var forbidden = new NpgsqlConnection(template.ConnectionString);
        var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.OpenAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, error.SqlState);
        Assert.Equal("false:false", await ScalarAsync(Admin(lease.ConnectionString),
            "SELECT datallowconn::text || ':' || datistemplate::text FROM pg_database WHERE datname = @name", owner.TemplateName));
        Assert.Equal(await ScalarAsync(Admin(lease.ConnectionString),
            "SELECT encoding::text || ':' || datcollate || ':' || datctype FROM pg_database WHERE datname = @name", owner.TemplateName),
            await ScalarAsync(Admin(lease.ConnectionString),
                "SELECT encoding::text || ':' || datcollate || ':' || datctype FROM pg_database WHERE datname = @name", lease.Name));
    }

    [Fact]
    public async Task FailedInitializationDropsPartialSchemaAndCannotBeReused()
    {
        var marker = new InvalidOperationException("Owned template initialization failure.");
        string? failedName = null;
        string? admin = null;
        var calls = 0;
        await using var owner = new PostgreSqlMigratedTemplate(async (db, ct) =>
        {
            calls++;
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"PartialOwnedSchema\" (\"Id\" integer)", ct);
            throw marker;
        });
        Assert.Same(marker, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync("pp_template_test_")));
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        Assert.Same(marker, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync("pp_template_test_")));
        Assert.Equal(1, calls);
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task CancelledInitializationDropsPartialSchemaAndCannotBeReused()
    {
        using var cancellation = new CancellationTokenSource();
        string? failedName = null;
        string? admin = null;
        var calls = 0;
        await using var owner = new PostgreSqlMigratedTemplate(async (db, ct) =>
        {
            calls++;
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"PartialOwnedSchema\" (\"Id\" integer)", ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync("pp_template_test_", cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        Assert.Same(error, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync("pp_template_test_")));
        Assert.Equal(1, calls);
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task IncompleteMigrationCannotPublishReadyTemplate()
    {
        string? failedName = null;
        string? admin = null;
        await using var owner = new PostgreSqlMigratedTemplate((db, _) =>
        {
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<Exception>(() => owner.CreateDatabaseAsync("pp_template_test_"));
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task OwnerRejectsDisposalWithActiveLeaseAndDoesNotTouchAnotherOwner()
    {
        await using var first = new PostgreSqlMigratedTemplate();
        await using var second = new PostgreSqlMigratedTemplate();
        await using var firstLease = await first.CreateDatabaseAsync("pp_template_test_");
        await using var secondLease = await second.CreateDatabaseAsync("pp_template_test_");
        var firstTemplate = first.TemplateName!;
        var secondTemplate = second.TemplateName!;
        Assert.NotEqual(firstTemplate, secondTemplate);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.DisposeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.CreateDatabaseAsync("pp_template_test_"));
        Assert.True(await ExistsAsync(Admin(firstLease.ConnectionString), firstTemplate));
        await firstLease.DisposeAsync();
        await first.DisposeAsync();
        Assert.False(await ExistsAsync(Admin(firstLease.ConnectionString), firstTemplate));
        Assert.False(await ExistsAsync(Admin(firstLease.ConnectionString), firstLease.Name));
        Assert.True(await ExistsAsync(Admin(secondLease.ConnectionString), secondTemplate));
        Assert.True(await ExistsAsync(Admin(secondLease.ConnectionString), secondLease.Name));
        await using var context = Context(secondLease);
        Assert.Equal("false", await context.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
    }

    [Fact]
    public async Task LiveCloneConnectionPreventsDropWithoutTerminatingItsSession()
    {
        await using var owner = new PostgreSqlMigratedTemplate();
        await using var lease = await owner.CreateDatabaseAsync("pp_template_test_");
        await using (var connection = new NpgsqlConnection(lease.ConnectionString))
        {
            await connection.OpenAsync();
            await Assert.ThrowsAnyAsync<Exception>(() => lease.DisposeAsync().AsTask());
            Assert.True(await ExistsAsync(Admin(lease.ConnectionString), lease.Name));
            await using var command = new NpgsqlCommand("SELECT 42", connection);
            Assert.Equal(42, await command.ExecuteScalarAsync());
        }
        await lease.DisposeAsync();
        Assert.False(await ExistsAsync(Admin(lease.ConnectionString), lease.Name));
    }

    [Fact]
    public async Task CancellingWaitingCloneDoesNotCancelTheInitializationOwner()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new PostgreSqlMigratedTemplate(async (db, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            await db.Database.MigrateAsync(ct);
        });
        var pending = owner.CreateDatabaseAsync("pp_template_test_");
        string? firstName = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var cancellation = new CancellationTokenSource();
            var waiting = owner.CreateDatabaseAsync("pp_template_test_", cancellation.Token);
            Assert.False(waiting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally
        {
            release.TrySetResult();
            await using var first = await pending;
            firstName = first.Name;
        }
        await using (var second = await owner.CreateDatabaseAsync("pp_template_test_"))
        {
            Assert.NotNull(firstName);
            Assert.NotEqual(firstName, second.Name);
            await using var context = Context(second);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        }
    }

    [Fact]
    public async Task PreCancelledRequestNeverCreatesTemplate()
    {
        await using var owner = new PostgreSqlMigratedTemplate((_, _) => throw new InvalidOperationException("Unexpected migration."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync("pp_template_test_", cancellation.Token));
        Assert.Null(owner.TemplateName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("postgres")]
    [InlineData("pp_foreign\"_")]
    [InlineData("pp_bad-name_")]
    [InlineData("pp_abcdefghijklmnopqrstuvwxyzx_")]
    public async Task InvalidOwnedPrefixNeverCreatesTemplate(string prefix)
    {
        await using var owner = new PostgreSqlMigratedTemplate((_, _) => throw new InvalidOperationException("Unexpected migration."));
        await Assert.ThrowsAsync<ArgumentException>(() => owner.CreateDatabaseAsync(prefix));
        Assert.Null(owner.TemplateName);
    }

    private static PostgreSqlPaymentContext Context(PostgreSqlMigratedTemplate.DatabaseLease lease)
        => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);

    private static string Admin(string connectionString)
        => new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false }.ConnectionString;

    private static async Task<bool> ExistsAsync(string admin, string name)
        => (long)(await ScalarAsync(admin, "SELECT count(*) FROM pg_database WHERE datname = @name", name))! == 1;

    private static async Task<object?> ScalarAsync(string connectionString, string sql, string? name = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        if (name is not null) command.Parameters.AddWithValue("name", name);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<string[]> CatalogAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT 'column:' || c.relname || ':' || a.attname || ':' || format_type(a.atttypid,a.atttypmod)
                || ':' || a.attnotnull || ':' || coalesce(pg_get_expr(d.adbin,d.adrelid),'') AS definition
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            UNION ALL SELECT 'index:' || tablename || ':' || indexdef FROM pg_indexes WHERE schemaname='public'
            UNION ALL SELECT 'constraint:' || c.relname || ':' || x.conname || ':' || pg_get_constraintdef(x.oid)
            FROM pg_constraint x JOIN pg_class c ON c.oid=x.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
            WHERE n.nspname='public'
            ORDER BY definition
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }
}
