using System.Diagnostics;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PostgreSqlModelTemplateTests
{
    [Fact]
    public async Task ModelCloneMatchesNativeEnsureCreatedCatalogSeedsAndHasNoMigrationHistory()
    {
        var started = Stopwatch.GetTimestamp();
        var native = await BankImportProvenanceModelTests.ModelDatabase.CreateAsync(postgres: true);
        Console.WriteLine($"Owned bank-model native control; freshCreateMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        try
        {
            await using var owner = new PostgreSqlModelTemplate();
            await using var lease = await owner.CreateDatabaseAsync();
            await using var clone = Context(lease);
            var originalConnection = native.Db.Database.GetConnectionString()!;
            Assert.Equal(native.Db.Database.GenerateCreateScript(), clone.Database.GenerateCreateScript());
            Assert.Equal(await CatalogAsync(originalConnection), await CatalogAsync(lease.ConnectionString));
            foreach (var db in new[] { native.Db, clone })
            {
                Assert.Empty(await db.Users.ToArrayAsync());
                Assert.Empty(await db.Clients.ToArrayAsync());
                Assert.Empty(await db.Payments.ToArrayAsync());
                Assert.Empty(await db.MoneyAccounts.ToArrayAsync());
                Assert.Equal(new[] { 1, 2, 3 }, await db.GeneratedFinancialNumberScopes.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync());
                Assert.Equal(0L, (await db.StorageCapacityEpochs.SingleAsync()).Version);
                Assert.Equal("false", (await db.SystemSettings.SingleAsync()).Value);
                Assert.Null(await ScalarAsync(db.Database.GetConnectionString()!, "SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text"));
            }
            Assert.Equal("false:false", await ScalarAsync(Admin(lease.ConnectionString),
                "SELECT datallowconn::text || ':' || datistemplate::text FROM pg_database WHERE datname=@name", owner.TemplateName));
            var admin = Admin(lease.ConnectionString);
            const string databaseTablespace = "SELECT t.spcname FROM pg_database d JOIN pg_tablespace t ON t.oid=d.dattablespace WHERE d.datname=@name";
            var templateTablespace = await ScalarAsync(admin, databaseTablespace, owner.TemplateName);
            var cloneTablespace = await ScalarAsync(admin, databaseTablespace, lease.Name);
            Assert.NotNull(templateTablespace);
            Assert.Equal(templateTablespace, cloneTablespace);
            var configuredTablespace = Assert.IsType<string>(await ScalarAsync(admin, "SELECT current_setting('default_tablespace')"));
            if (configuredTablespace.Length != 0) Assert.Equal(configuredTablespace, cloneTablespace);
            var blocked = new NpgsqlConnectionStringBuilder(lease.ConnectionString) { Database = owner.TemplateName };
            await using var forbidden = new NpgsqlConnection(blocked.ConnectionString);
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.OpenAsync());
            Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, error.SqlState);
        }
        finally
        {
            started = Stopwatch.GetTimestamp();
            await native.DisposeAsync();
            Console.WriteLine($"Owned bank-model native control; freshDropMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
    }

    [Fact]
    public async Task ConcurrentIndependentClonesCreateModelOnceAndIsolateRowsSchemaSequencesAndTransactions()
    {
        var preparations = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) =>
        {
            Interlocked.Increment(ref preparations);
            await db.Database.EnsureCreatedAsync(ct);
        });
        var pending = Enumerable.Range(0, 3).Select(_ => owner.CreateDatabaseAsync()).ToArray();
        try
        {
            var leases = await Task.WhenAll(pending);
            Assert.Equal(1, preparations);
            Assert.Equal(3, leases.Select(row => row.Name).Distinct().Count());
            await using var first = Context(leases[0]);
            await using var second = Context(leases[1]);
            await using var third = Context(leases[2]);
            var left = new Client { Name = "model clone left" };
            var right = new Client { Name = "model clone right" };
            first.Clients.Add(left);
            second.Clients.Add(right);
            await first.SaveChangesAsync();
            await second.SaveChangesAsync();
            Assert.Equal(left.Id, right.Id);
            Assert.Equal("model clone left", (await first.Clients.SingleAsync()).Name);
            Assert.Equal("model clone right", (await second.Clients.SingleAsync()).Name);
            Assert.Empty(await third.Clients.ToArrayAsync());
            await first.Database.ExecuteSqlRawAsync("CREATE TABLE \"ModelCloneOnly\" (\"Id\" integer PRIMARY KEY)");
            Assert.Null(await ScalarAsync(leases[1].ConnectionString, "SELECT to_regclass('public.\"ModelCloneOnly\"')::text"));
            await using (var transaction = await third.Database.BeginTransactionAsync())
            {
                third.Clients.Add(new Client { Name = "rolled back clone" });
                await third.SaveChangesAsync();
                await transaction.RollbackAsync();
            }
            third.ChangeTracker.Clear();
            Assert.Empty(await third.Clients.ToArrayAsync());
            await using var later = await owner.CreateDatabaseAsync();
            await using var laterDb = Context(later);
            Assert.Empty(await laterDb.Clients.ToArrayAsync());
            Assert.Null(await ScalarAsync(later.ConnectionString, "SELECT to_regclass('public.\"ModelCloneOnly\"')::text"));
            Assert.Equal(1, preparations);
        }
        finally
        {
            foreach (var task in pending)
                if (task.IsCompletedSuccessfully) await (await task).DisposeAsync();
        }
    }

    [Fact]
    public async Task ModelDatabaseLeaseDisposalDoesNotDisposeClassOwnerAndKeepsOperationInterceptors()
    {
        await using var owner = new PostgreSqlModelTemplate();
        var database = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(true, owner);
        var name = database.Db.Database.GetDbConnection().Database;
        var connection = database.Db.Database.GetConnectionString()!;
        var calls = new OperationInterceptors();
        await using (var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
            .UseNpgsql(connection).AddInterceptors(calls, calls.Commands, calls.Transactions).Options))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Clients.Add(new Client { Name = "intercepted model clone" });
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            Assert.True(calls.Saves > 0);
            Assert.True(calls.CommandCalls > 0);
            Assert.True(calls.TransactionCalls > 0);
        }
        await database.DisposeAsync();
        await database.DisposeAsync();
        Assert.False(await ExistsAsync(Admin(connection), name));
        await using var next = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(true, owner);
        Assert.Empty(await next.Db.Clients.ToArrayAsync());
    }

    [Fact]
    public async Task InitializationFailureCleansAcknowledgedPartialSchemaAndIsSticky()
    {
        var expected = new InvalidOperationException("Synthetic model initialization failure.");
        string? failedName = null, admin = null;
        var calls = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) =>
        {
            calls++;
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"PartialBankModel\" (\"Id\" integer)", ct);
            throw expected;
        });
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync()));
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        Assert.Same(expected, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync()));
        Assert.Equal(1, calls);
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task IncompleteModelCannotPublishAReadyTemplate()
    {
        string? failedName = null, admin = null;
        await using var owner = new PostgreSqlModelTemplate((db, _) =>
        {
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<Exception>(() => owner.CreateDatabaseAsync());
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task CancelledInitializationCleansItsPartialDatabaseAndCannotSilentlyRetry()
    {
        using var cancellation = new CancellationTokenSource();
        string? failedName = null, admin = null;
        var attempts = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) =>
        {
            attempts++;
            failedName = db.Database.GetDbConnection().Database;
            admin = Admin(db.Database.GetConnectionString()!);
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"CancelledBankModel\" (\"Id\" integer)", ct);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
        });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync(cancellation.Token));
        Assert.False(await ExistsAsync(admin!, failedName!));
        Assert.Same(error, await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync()));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancelledAcknowledgedCloneCleansOnlyItsLeaseAndKeepsTemplateReady()
    {
        using var cancellation = new CancellationTokenSource();
        string? failedName = null, admin = null;
        var attempts = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); },
            (connection, ct) =>
            {
                if (++attempts == 1)
                {
                    failedName = new NpgsqlConnectionStringBuilder(connection).Database;
                    admin = Admin(connection);
                    cancellation.Cancel();
                    ct.ThrowIfCancellationRequested();
                }
                return Task.CompletedTask;
            });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        await using var lease = await owner.CreateDatabaseAsync();
        Assert.NotEqual(failedName, lease.Name);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task FailedContextReadinessDropsItsCloneWithoutChangingTheProtectedTemplate()
    {
        string? failedName = null, admin = null;
        var attempts = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); },
            async (connection, ct) =>
            {
                if (++attempts != 1) return;
                failedName = new NpgsqlConnectionStringBuilder(connection).Database;
                admin = Admin(connection);
                await using var db = new NpgsqlConnection(connection);
                await db.OpenAsync(ct);
                await using var command = new NpgsqlCommand("UPDATE \"SystemSettings\" SET \"Value\"='true' WHERE \"Key\"='notifications.email.enabled'", db);
                await command.ExecuteNonQueryAsync(ct);
            });
        await Assert.ThrowsAnyAsync<Exception>(() => BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(true, owner));
        Assert.NotNull(failedName);
        Assert.False(await ExistsAsync(admin!, failedName));
        await using var next = await BankImportProvenanceModelTests.ModelDatabase.CreateFromModelTemplateAsync(true, owner);
        Assert.Equal("false", (await next.Db.SystemSettings.SingleAsync()).Value);
    }

    [Fact]
    public async Task LiveCloneSessionPreventsDropWithoutTerminatingItsConnection()
    {
        await using var owner = new PostgreSqlModelTemplate();
        await using var lease = await owner.CreateDatabaseAsync();
        await using var live = new NpgsqlConnection(lease.ConnectionString);
        await live.OpenAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());
        await using (var query = new NpgsqlCommand("SELECT 1", live)) Assert.Equal(1, await query.ExecuteScalarAsync());
        await live.CloseAsync();
        await lease.DisposeAsync();
        Assert.False(await ExistsAsync(Admin(lease.ConnectionString), lease.Name));
    }

    [Fact]
    public async Task OwnerDisposalRejectsActiveLeasesAndCannotTouchAnotherOwner()
    {
        await using var first = new PostgreSqlModelTemplate();
        await using var second = new PostgreSqlModelTemplate();
        await using var left = await first.CreateDatabaseAsync();
        await using var right = await second.CreateDatabaseAsync();
        Assert.NotEqual(first.TemplateName, second.TemplateName);
        var templateName = first.TemplateName!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.DisposeAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.CreateDatabaseAsync());
        await left.DisposeAsync();
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.False(await ExistsAsync(Admin(left.ConnectionString), templateName));
        Assert.True(await ExistsAsync(Admin(right.ConnectionString), second.TemplateName!));
        await using var db = Context(right);
        Assert.Equal("false", (await db.SystemSettings.SingleAsync()).Value);
    }

    [Fact]
    public async Task ChangedDatabaseMarkerRefusesCleanupAndPreservesDataUntilExplicitlyRestored()
    {
        await using var owner = new PostgreSqlModelTemplate();
        await using var lease = await owner.CreateDatabaseAsync();
        await using (var db = Context(lease))
        {
            db.Clients.Add(new Client { Name = "must survive refused cleanup" });
            await db.SaveChangesAsync();
        }
        var admin = Admin(lease.ConnectionString);
        try
        {
            await SetMarkerAsync(admin, lease.Name, "synthetic-foreign-marker");
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.DisposeAsync().AsTask());
            Assert.True(await ExistsAsync(admin, lease.Name));
            await using var verify = Context(lease);
            Assert.Equal("must survive refused cleanup", (await verify.Clients.SingleAsync()).Name);
        }
        finally { await SetMarkerAsync(admin, lease.Name, lease.Database.Marker); }
    }

    [Theory]
    [InlineData("marker")]
    [InlineData("protection")]
    public async Task ChangedSourceTemplateCannotCreateAnotherClone(string change)
    {
        var publishedClones = 0;
        await using var owner = new PostgreSqlModelTemplate(async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); },
            (_, _) => { publishedClones++; return Task.CompletedTask; });
        await using var first = await owner.CreateDatabaseAsync();
        var admin = Admin(first.ConnectionString);
        var name = owner.TemplateName!;
        var marker = Assert.IsType<string>(await ScalarAsync(admin, "SELECT shobj_description(oid,'pg_database') FROM pg_database WHERE datname=@name", name));
        await first.DisposeAsync();
        try
        {
            if (change == "marker") await SetMarkerAsync(admin, name, "synthetic-source-marker-drift");
            else await SetProtectionAsync(admin, name, true);
            PostgreSqlModelTemplate.DatabaseLease? unexpected = null;
            try
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () => { unexpected = await owner.CreateDatabaseAsync(); });
                Assert.Equal(1, publishedClones);
                Assert.Equal(name, owner.TemplateName);
            }
            finally { if (unexpected is not null) await unexpected.DisposeAsync(); }
        }
        finally
        {
            await SetMarkerAsync(admin, name, marker);
            await SetProtectionAsync(admin, name, false);
        }
        await using var restored = await owner.CreateDatabaseAsync();
        Assert.Equal(2, publishedClones);
        await using var verify = Context(restored);
        Assert.Empty(await verify.Clients.ToArrayAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("remote")]
    public async Task InitialConfigurationFailureIsStickyWithoutReadingReplacementSettings(string kind)
    {
        var reads = 0;
        await using var owner = new PostgreSqlModelTemplate((_, _) => throw new InvalidOperationException("Unexpected schema creation."),
            postgresConnectionString: () => ++reads == 1
                ? kind == "missing" ? null : "Host=192.0.2.1;Database=postgres;Pooling=false"
                : "Host=127.0.0.1;Database=postgres;Pooling=false");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync());
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => owner.CreateDatabaseAsync()));
        Assert.Equal(1, reads);
        Assert.Null(owner.TemplateName);
    }

    [Fact]
    public async Task UnrequestedOrPreCancelledClassOwnerCreatesNoDatabase()
    {
        var calls = 0;
        await using var owner = new PostgreSqlModelTemplate((_, _) => { calls++; throw new InvalidOperationException("Unexpected schema creation."); });
        await owner.InitializeAsync();
        Assert.Null(owner.TemplateName);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.CreateDatabaseAsync(cancellation.Token));
        Assert.Equal(0, calls);
        Assert.Null(owner.TemplateName);
    }

    [Theory]
    [InlineData("oid")]
    [InlineData("owner")]
    [InlineData("marker")]
    [InlineData("missing-marker")]
    public void ChangedIdentityIsNeverCleanupProof(string kind)
        => Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireIdentity(10, "limited", "marker",
            kind == "oid" ? 11 : 10, kind == "owner" ? "foreign" : "limited", kind == "missing-marker" ? null : kind == "marker" ? "foreign" : "marker"));

    [Theory]
    [InlineData(false, false, null)]
    [InlineData(true, false, 10L)]
    [InlineData(true, true, null)]
    public void AmbiguousOrIncompleteCreateNeverAuthorizesCleanup(bool acknowledged, bool markerWritten, long? oid)
        => Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireCreateProof(acknowledged, markerWritten, oid));

    [Fact]
    public void MarkerRetryRequiresTheAlreadyRecordedOidAndCannotReclaimByName()
    {
        Assert.False(PostgreSqlModelTemplate.CanRetryMarkerRegistration(true, null));
        Assert.False(PostgreSqlModelTemplate.CanRetryMarkerRegistration(false, 10));
        Assert.True(PostgreSqlModelTemplate.CanRetryMarkerRegistration(true, 10));
        Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireMarkerRegistrationIdentity(10, "limited", "ours", 11, "limited", null));
        Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireMarkerRegistrationIdentity(10, "limited", "ours", 10, "foreign", null));
        Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireMarkerRegistrationIdentity(10, "limited", "ours", 10, "limited", "foreign"));
        PostgreSqlModelTemplate.RequireMarkerRegistrationIdentity(10, "limited", "ours", 10, "limited", "ours");
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("pp_bank_model_missing")]
    [InlineData("pp_bank_model_0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("pp_bank_model_bad-0123456789abcdef0123456789abcdef")]
    public void UnrecognizedNamesAreRejectedBeforeSql(string name)
        => Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireOwnedName(name));

    [Theory]
    [InlineData("remote")]
    [InlineData("pool")]
    [InlineData("database")]
    public void UnsafeControlSettingsAreRejectedBeforeConnection(string kind)
    {
        var settings = new NpgsqlConnectionStringBuilder { Host = kind == "remote" ? "192.0.2.1" : "127.0.0.1", Database = kind == "database" ? "foreign" : "postgres", Pooling = kind == "pool" };
        Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireLoopback(settings));
    }

    [Theory]
    [InlineData("createdb")]
    [InlineData("superuser")]
    [InlineData("createrole")]
    [InlineData("replication")]
    [InlineData("bypassrls")]
    public void UnsafeRoleMetadataIsRejectedBeforeCreate(string kind)
        => Assert.Throws<InvalidOperationException>(() => PostgreSqlModelTemplate.RequireLimitedRole(kind != "createdb", kind == "superuser", kind == "createrole", kind == "replication", kind == "bypassrls"));

    private sealed class OperationInterceptors : SaveChangesInterceptor
    {
        internal int Saves;
        internal int CommandCalls;
        internal int TransactionCalls;
        internal DbCommandInterceptor Commands => new CommandsInterceptor(this);
        internal DbTransactionInterceptor Transactions => new TransactionsInterceptor(this);
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        {
            Saves++;
            return ValueTask.FromResult(result);
        }
        private sealed class CommandsInterceptor(OperationInterceptors owner) : DbCommandInterceptor
        {
            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
            {
                owner.CommandCalls++;
                return ValueTask.FromResult(result);
            }
        }
        private sealed class TransactionsInterceptor(OperationInterceptors owner) : DbTransactionInterceptor
        {
            public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData data, InterceptionResult<DbTransaction> result, CancellationToken ct = default)
            {
                owner.TransactionCalls++;
                return ValueTask.FromResult(result);
            }
        }
    }

    private static PostgreSqlPaymentContext Context(PostgreSqlModelTemplate.DatabaseLease lease)
        => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options);

    private static string Admin(string connection)
        => new NpgsqlConnectionStringBuilder(connection) { Database = "postgres", Pooling = false }.ConnectionString;

    private static async Task<bool> ExistsAsync(string admin, string name)
        => (long)(await ScalarAsync(admin, "SELECT count(*) FROM pg_database WHERE datname=@name", name))! == 1;

    private static async Task<object?> ScalarAsync(string connectionString, string sql, string? name = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand(sql, connection);
        if (name is not null) query.Parameters.AddWithValue("name", name);
        var result = await query.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }

    private static async Task SetMarkerAsync(string admin, string name, string marker)
    {
        PostgreSqlModelTemplate.RequireOwnedName(name);
        Assert.DoesNotContain("'", marker);
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"COMMENT ON DATABASE \"{name}\" IS '{marker}'", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetProtectionAsync(string admin, string name, bool allowConnections)
    {
        PostgreSqlModelTemplate.RequireOwnedName(name);
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        var value = allowConnections ? "true" : "false";
        await using var command = new NpgsqlCommand($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS {value}", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string[]> CatalogAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("""
            SELECT 'column:' || c.relname || ':' || a.attname || ':' || format_type(a.atttypid,a.atttypmod)
                || ':' || a.attnotnull || ':' || coalesce(pg_get_expr(d.adbin,d.adrelid),'') AS definition
            FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
            JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
            LEFT JOIN pg_attrdef d ON d.adrelid=c.oid AND d.adnum=a.attnum
            WHERE n.nspname='public' AND c.relkind IN ('r','p')
            UNION ALL SELECT 'index:' || tablename || ':' || indexdef FROM pg_indexes WHERE schemaname='public'
            UNION ALL SELECT 'constraint:' || c.relname || ':' || x.conname || ':' || pg_get_constraintdef(x.oid)
            FROM pg_constraint x JOIN pg_class c ON c.oid=x.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public'
            UNION ALL SELECT 'sequence:' || sequencename || ':' || data_type || ':' || start_value || ':' || min_value || ':' || max_value || ':' || increment_by || ':' || cycle || ':' || cache_size
            FROM pg_sequences WHERE schemaname='public'
            ORDER BY definition
            """, connection);
        await using var reader = await query.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return rows.ToArray();
    }
}
