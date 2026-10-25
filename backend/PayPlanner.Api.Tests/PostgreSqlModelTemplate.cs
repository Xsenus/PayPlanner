using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>A class-owned EnsureCreated schema; every lease owns an independent database.</summary>
public sealed class PostgreSqlModelTemplate : IAsyncLifetime, IAsyncDisposable
{
    private const int DatabaseLifecycleTimeoutSeconds = 120;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, OwnedDatabase> owned = [];
    private readonly Dictionary<string, DatabaseLease> leases = [];
    private readonly Func<PostgreSqlPaymentContext, CancellationToken, Task> ensureCreated;
    private readonly Func<string, CancellationToken, Task>? beforeLease;
    private readonly Func<string?> readConnectionString;
    private string? adminConnection;
    private EndpointIdentity? endpoint;
    private OwnedDatabase? template;
    private ExceptionDispatchInfo? initializationFailure;
    private bool ready;
    private bool closing;
    private bool disposed;

    public PostgreSqlModelTemplate() : this(async (db, ct) => { await db.Database.EnsureCreatedAsync(ct); }) { }

    // These hooks inject failures only in the test helper, without weakening readiness/ownership checks.
    internal PostgreSqlModelTemplate(Func<PostgreSqlPaymentContext, CancellationToken, Task> ensureCreated,
        Func<string, CancellationToken, Task>? beforeLease = null, Func<string?>? postgresConnectionString = null)
    {
        this.ensureCreated = ensureCreated;
        this.beforeLease = beforeLease;
        readConnectionString = postgresConnectionString ?? (() => Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES"));
    }

    public Task InitializeAsync() => Task.CompletedTask;
    internal string? TemplateName => template?.Name;

    internal async Task<DatabaseLease> CreateDatabaseAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
            initializationFailure?.Throw();
            await EnsureReadyAsync(ct);
            var started = Stopwatch.GetTimestamp();
            var database = await CreateOwnedAsync("pp_bank_model_" + Guid.NewGuid().ToString("N"), template!.Name, ct);
            try
            {
                var connection = new NpgsqlConnectionStringBuilder(adminConnection!) { Database = database.Name }.ConnectionString;
                if (beforeLease is not null) await beforeLease(connection, ct);
                ct.ThrowIfCancellationRequested();
                var lease = new DatabaseLease(this, database, connection);
                leases.Add(database.Name, lease);
                Report($"Owned bank-model database created: {database.Name}; cloneMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
                return lease;
            }
            catch (Exception error)
            {
                try { await DropOwnedAsync(database); }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (ready) return;
        var started = Stopwatch.GetTimestamp();
        try
        {
            var raw = readConnectionString();
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("Bank model templates require PAYPLANNER_TEST_POSTGRES.");
            var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
            RequireLoopback(settings);
            adminConnection = settings.ConnectionString;
            template = await CreateOwnedAsync("pp_bank_model_template_" + Guid.NewGuid().ToString("N"), null, ct);
            settings.Database = template.Name;
            await using (var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options))
            {
                await ensureCreated(db, ct);
                Assert.False(db.Database.HasPendingModelChanges());
                Assert.Empty(await db.Users.ToArrayAsync(ct));
                Assert.Empty(await db.Clients.ToArrayAsync(ct));
                Assert.Empty(await db.Payments.ToArrayAsync(ct));
                Assert.Empty(await db.MoneyAccounts.ToArrayAsync(ct));
                Assert.Empty(await db.BankStatementImportBatches.ToArrayAsync(ct));
                Assert.Equal(new[] { 1, 2, 3 }, await db.GeneratedFinancialNumberScopes.OrderBy(row => row.Id).Select(row => row.Id).ToArrayAsync(ct));
                Assert.Equal(0L, (await db.StorageCapacityEpochs.SingleAsync(ct)).Version);
                Assert.Empty(await db.SystemSettings.ToArrayAsync(ct));
                db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                await db.SaveChangesAsync(ct);
            }
            await using (var schema = new NpgsqlConnection(settings.ConnectionString))
            {
                await schema.OpenAsync(ct);
                await using var history = new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"')::text", schema);
                Assert.Equal(DBNull.Value, await history.ExecuteScalarAsync(ct));
            }
            await using (var admin = await OpenAdminAsync(ct))
            {
                await RequireOwnedAsync(admin, template, ct);
                await RequireNoSessionsAsync(admin, template.Name);
                await using var protect = new NpgsqlCommand($"ALTER DATABASE \"{template.Name}\" ALLOW_CONNECTIONS false", admin);
                await protect.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            ready = true;
            Report($"Owned bank-model template: {template.Name}; prepareMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
        catch (Exception error)
        {
            initializationFailure = ExceptionDispatchInfo.Capture(error);
            if (template is not null && owned.ContainsKey(template.Name))
            {
                try { await DropOwnedAsync(template); template = null; }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            }
            else template = null;
            throw;
        }
    }

    internal static void RequireLoopback(NpgsqlConnectionStringBuilder settings)
    {
        if (settings.Host is not ("127.0.0.1" or "localhost" or "::1") || settings.Database != "postgres"
            || settings.Pooling || settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("Model templates require an unpooled loopback PostgreSQL control connection.");
    }

    internal static void RequireOwnedName(string name)
    {
        var prefix = name.StartsWith("pp_bank_model_template_", StringComparison.Ordinal) ? "pp_bank_model_template_" : "pp_bank_model_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length != prefix.Length + 32
            || !Guid.TryParseExact(name[^32..], "N", out _)
            || name.Any(c => c != '_' && c is not (>= 'a' and <= 'z') && c is not (>= '0' and <= '9')))
            throw new InvalidOperationException("An exact owned bank-model database name is required.");
    }

    internal static void RequireLimitedRole(bool createDb, bool superuser, bool createRole, bool replication, bool bypassRls)
    {
        if (!createDb || superuser || createRole || replication || bypassRls)
            throw new InvalidOperationException("Model template role must have CREATEDB without elevated role privileges.");
    }

    private async Task<NpgsqlConnection> OpenAdminAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(adminConnection!);
        try
        {
            await connection.OpenAsync(ct);
            await using var query = new NpgsqlCommand("SELECT d.oid::bigint,current_user,host(inet_server_addr()),inet_server_port(),r.rolcreatedb,r.rolsuper,r.rolcreaterole,r.rolreplication,r.rolbypassrls FROM pg_database d JOIN pg_roles r ON r.rolname=current_user WHERE d.datname=current_database() AND current_database()='postgres'", connection);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("PostgreSQL control role is unavailable.");
            RequireLimitedRole(reader.GetBoolean(4), reader.GetBoolean(5), reader.GetBoolean(6), reader.GetBoolean(7), reader.GetBoolean(8));
            var actual = new EndpointIdentity(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
            var configured = new NpgsqlConnectionStringBuilder(adminConnection!);
            if (actual.ControlOid <= 0 || actual.Host is not ("127.0.0.1" or "::1") || actual.Port != configured.Port
                || !string.IsNullOrEmpty(configured.Username) && configured.Username != actual.Role)
                throw new InvalidOperationException("Unexpected PostgreSQL control identity.");
            endpoint ??= actual;
            if (endpoint != actual) throw new InvalidOperationException("PostgreSQL endpoint/control role identity changed.");
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task<OwnedDatabase> CreateOwnedAsync(string name, string? source, CancellationToken ct)
    {
        RequireOwnedName(name);
        if (source is not null) RequireOwnedName(source);
        await using var admin = await OpenAdminAsync(ct);
        if (source is not null)
        {
            if (template is null || source != template.Name || !owned.TryGetValue(source, out var registered)
                || !ReferenceEquals(template, registered))
                throw new InvalidOperationException("Clone source is not this owner's ready template.");
            await RequireOwnedAsync(admin, template, ct);
            await using var protectedSource = new NpgsqlCommand("SELECT datallowconn FROM pg_database WHERE oid::bigint=@oid AND datname=@name", admin);
            protectedSource.Parameters.AddWithValue("oid", template.Oid.GetValueOrDefault());
            protectedSource.Parameters.AddWithValue("name", source);
            if (await protectedSource.ExecuteScalarAsync(ct) is not false)
                throw new InvalidOperationException("Refusing to clone a template whose connection protection changed.");
        }
        await using (var absent = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname=@name", admin))
        {
            absent.Parameters.AddWithValue("name", name);
            Assert.Equal(0L, (long)(await absent.ExecuteScalarAsync(ct))!);
        }
        var sourceClause = $" TEMPLATE \"{source}\"";
        if (source is null)
        {
            await using var readTablespace = new NpgsqlCommand("SELECT current_setting('default_tablespace')", admin);
            var tablespace = (string)(await readTablespace.ExecuteScalarAsync(ct))!;
            // default_tablespace alone controls relations, not the database default.
            // Set the source explicitly so every owned clone inherits its location.
            sourceClause = PostgreSqlMigratedTemplate.TablespaceClause(tablespace);
        }
        var database = new OwnedDatabase(name, "pp-bank-model:" + Guid.NewGuid().ToString("N"));
        owned.Add(name, database);
        try
        {
            var sql = $"CREATE DATABASE \"{name}\"" + sourceClause;
            await using var create = new NpgsqlCommand(sql, admin)
            { CommandTimeout = DatabaseLifecycleTimeoutSeconds };
            await create.ExecuteNonQueryAsync(ct);
            database.CreateAcknowledged = true;
            await MarkAcknowledgedAsync(admin, database, ct);
            return database;
        }
        catch (Exception error)
        {
            if (error is PostgresException { SqlState: PostgresErrorCodes.DuplicateDatabase })
            {
                owned.Remove(name);
                throw;
            }
            if (!CanRetryMarkerRegistration(database.CreateAcknowledged, database.Oid))
                throw new InvalidOperationException($"Ambiguous CREATE/OID outcome; database is preserved without ownership proof: {name}", error);
            try
            {
                if (!database.MarkerWritten)
                {
                    await using var retryAdmin = await OpenAdminAsync();
                    await MarkAcknowledgedAsync(retryAdmin, database, CancellationToken.None);
                }
                await DropOwnedAsync(database);
            }
            catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            throw;
        }
    }

    private async Task MarkAcknowledgedAsync(NpgsqlConnection admin, OwnedDatabase database, CancellationToken ct)
    {
        if (!database.CreateAcknowledged) throw new InvalidOperationException("Cannot claim an unacknowledged CREATE.");
        await using (var query = new NpgsqlCommand("SELECT oid::bigint,pg_get_userbyid(datdba),shobj_description(oid,'pg_database') FROM pg_database WHERE datname=@name", admin))
        {
            query.Parameters.AddWithValue("name", database.Name);
            await using var reader = await query.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Acknowledged database disappeared before marker registration.");
            var oid = reader.GetInt64(0);
            RequireMarkerRegistrationIdentity(database.Oid, endpoint!.Role, database.Marker,
                oid, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            database.Oid ??= oid;
        }
        await using var mark = new NpgsqlCommand($"COMMENT ON DATABASE \"{database.Name}\" IS '{database.Marker}'", admin);
        await mark.ExecuteNonQueryAsync(ct);
        database.MarkerWritten = true;
        await RequireOwnedAsync(admin, database, ct);
    }

    internal static bool CanRetryMarkerRegistration(bool acknowledged, long? knownOid)
        => acknowledged && knownOid is > 0;

    internal static void RequireMarkerRegistrationIdentity(long? knownOid, string expectedOwner, string expectedMarker,
        long actualOid, string actualOwner, string? actualMarker)
    {
        if (actualOid <= 0 || knownOid is not null && knownOid != actualOid || actualOwner != expectedOwner
            || actualMarker is not null && actualMarker != expectedMarker)
            throw new InvalidOperationException("Refusing marker registration after an OID, owner or existing marker change.");
    }

    internal static void RequireIdentity(long expectedOid, string expectedOwner, string expectedMarker,
        long actualOid, string actualOwner, string? actualMarker)
    {
        if (expectedOid <= 0 || actualOid != expectedOid || expectedOwner != actualOwner || expectedMarker != actualMarker)
            throw new InvalidOperationException("Refusing database cleanup after an OID, owner or marker change.");
    }

    internal static void RequireCreateProof(bool acknowledged, bool markerWritten, long? oid)
    {
        if (!acknowledged || !markerWritten || oid is null or <= 0)
            throw new InvalidOperationException("An unproven CREATE outcome cannot authorize cleanup.");
    }

    private async Task RequireOwnedAsync(NpgsqlConnection admin, OwnedDatabase database, CancellationToken ct = default)
    {
        RequireOwnedName(database.Name);
        RequireCreateProof(database.CreateAcknowledged, database.MarkerWritten, database.Oid);
        await using var query = new NpgsqlCommand("SELECT oid::bigint,pg_get_userbyid(datdba),shobj_description(oid,'pg_database') FROM pg_database WHERE datname=@name", admin);
        query.Parameters.AddWithValue("name", database.Name);
        await using var reader = await query.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException($"Owned database disappeared before cleanup: {database.Name}");
        RequireIdentity(database.Oid.GetValueOrDefault(), endpoint!.Role, database.Marker, reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task RequireNoSessionsAsync(NpgsqlConnection admin, string name)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=@name", admin);
            sessions.Parameters.AddWithValue("name", name);
            if ((long)(await sessions.ExecuteScalarAsync())! == 0) return;
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(5))
                throw new InvalidOperationException($"Owned model database still has active sessions; preserved: {name}");
            await Task.Delay(50);
        }
    }

    private async Task DropOwnedAsync(OwnedDatabase database)
    {
        RequireOwnedName(database.Name);
        if (!owned.TryGetValue(database.Name, out var known) || !ReferenceEquals(known, database))
            throw new InvalidOperationException("Database does not belong to this model template owner.");
        RequireCreateProof(database.CreateAcknowledged, database.MarkerWritten, database.Oid);
        var started = Stopwatch.GetTimestamp();
        await using var admin = await OpenAdminAsync();
        await RequireOwnedAsync(admin, database);
        await RequireNoSessionsAsync(admin, database.Name);
        await RequireOwnedAsync(admin, database);
        await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database.Name}\"", admin)
        { CommandTimeout = DatabaseLifecycleTimeoutSeconds };
        await drop.ExecuteNonQueryAsync();
        owned.Remove(database.Name);
        Report($"Owned bank-model database dropped: {database.Name}; dropMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
    }

    private async ValueTask ReleaseAsync(DatabaseLease lease)
    {
        await gate.WaitAsync();
        try
        {
            if (lease.Released) return;
            if (!leases.TryGetValue(lease.Name, out var known) || !ReferenceEquals(known, lease))
                throw new InvalidOperationException("Database lease does not belong to this model template owner.");
            await DropOwnedAsync(lease.Database);
            leases.Remove(lease.Name);
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
            if (leases.Count != 0) throw new InvalidOperationException($"Cannot dispose model template with {leases.Count} active leases.");
            foreach (var database in owned.Values.ToArray()) await DropOwnedAsync(database);
            template = null;
            disposed = true;
        }
        finally { gate.Release(); }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    private static void Report(string message)
    {
        try { Console.WriteLine(message); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { /* Diagnostics must not lose ownership of a database. */ }
    }

    private sealed record EndpointIdentity(long ControlOid, string Role, string Host, int Port);
    internal sealed class OwnedDatabase(string name, string marker)
    {
        internal string Name { get; } = name;
        internal string Marker { get; } = marker;
        internal long? Oid { get; set; }
        internal bool CreateAcknowledged { get; set; }
        internal bool MarkerWritten { get; set; }
    }

    internal sealed class DatabaseLease(PostgreSqlModelTemplate owner, OwnedDatabase database, string connectionString) : IAsyncDisposable
    {
        internal OwnedDatabase Database { get; } = database;
        internal string Name => Database.Name;
        internal string ConnectionString { get; } = connectionString;
        internal bool Released { get; set; }
        public ValueTask DisposeAsync() => owner.ReleaseAsync(this);
    }
}
