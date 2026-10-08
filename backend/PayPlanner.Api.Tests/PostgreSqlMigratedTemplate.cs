using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

/// <summary>A class-owned, schema-only template; each lease owns a separate database.</summary>
public sealed class PostgreSqlMigratedTemplate : IAsyncLifetime, IAsyncDisposable
{
    private const string TemplatePrefix = "pp_schema_template_";
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, DatabaseLease> leases = [];
    private readonly HashSet<string> pendingCleanup = [];
    private readonly Func<PostgreSqlPaymentContext, CancellationToken, Task> migrate;
    private string? adminConnectionString;
    private string? templateName;
    private ExceptionDispatchInfo? initializationFailure;
    private bool ready;
    private bool closing;
    private bool disposed;

    public PostgreSqlMigratedTemplate() : this((db, ct) => db.Database.MigrateAsync(ct)) { }

    // Failure injection is confined to this test helper; readiness still checks the real schema.
    internal PostgreSqlMigratedTemplate(Func<PostgreSqlPaymentContext, CancellationToken, Task> migrate)
        => this.migrate = migrate;

    public Task InitializeAsync() => Task.CompletedTask;
    internal string? TemplateName => templateName;

    internal async Task<DatabaseLease> CreateDatabaseAsync(string prefix, CancellationToken ct = default)
    {
        if (prefix.Length is < 4 or > 30 || !prefix.StartsWith("pp_", StringComparison.Ordinal)
            || !prefix.EndsWith('_') || prefix.Any(c => c != '_' && c is not (>= 'a' and <= 'z')))
            throw new ArgumentException("An owned lowercase PostgreSQL database prefix is required.", nameof(prefix));

        await gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
            initializationFailure?.Throw();
            await EnsureReadyAsync(ct);
            var name = prefix + Guid.NewGuid().ToString("N");
            await using var admin = await OpenAdminAsync(ct);
            var created = false;
            try
            {
                // A successful preflight reserves only this unpredictable, exact owned name.
                await RequireAbsentAsync(admin, name, ct);
                created = true; // Reconcile an unknown CREATE outcome during cancellation as well.
                pendingCleanup.Add(name);
                var started = Stopwatch.GetTimestamp();
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE \"{templateName}\"", admin);
                await create.ExecuteNonQueryAsync(ct);
                var settings = new NpgsqlConnectionStringBuilder(adminConnectionString!) { Database = name };
                var lease = new DatabaseLease(this, name, settings.ConnectionString);
                leases.Add(name, lease);
                pendingCleanup.Remove(name);
                Report($"Owned PostgreSQL clone: {name}; createMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
                return lease;
            }
            catch (Exception error)
            {
                if (error is PostgresException { SqlState: PostgresErrorCodes.DuplicateDatabase })
                {
                    created = false;
                    pendingCleanup.Remove(name);
                }
                if (created)
                {
                    try { await DropDatabaseAsync(name); pendingCleanup.Remove(name); }
                    catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task EnsureReadyAsync(CancellationToken ct)
    {
        if (ready) return;
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Owned PostgreSQL tests require PAYPLANNER_TEST_POSTGRES.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        if (settings.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Owned PostgreSQL templates require a loopback endpoint.");
        adminConnectionString = settings.ConnectionString;
        var name = TemplatePrefix + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            await using (var admin = await OpenAdminAsync(ct))
            {
                await RequireAbsentAsync(admin, name, ct);
                created = true;
                templateName = name;
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
                await create.ExecuteNonQueryAsync(ct);
            }
            settings.Database = name;
            var started = Stopwatch.GetTimestamp();
            int migrations;
            await using (var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options))
            {
                await migrate(db, ct);
                Assert.False(db.Database.HasPendingModelChanges());
                var expected = db.Database.GetMigrations().ToArray();
                Assert.NotEmpty(expected);
                Assert.Equal(expected, (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync(ct));
                migrations = expected.Length;
                Assert.Empty(await db.Users.ToArrayAsync(ct));
                var email = await db.SystemSettings.SingleOrDefaultAsync(row => row.Key == "notifications.email.enabled", ct);
                if (email is null) db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
                else email.Value = "false";
                await db.SaveChangesAsync(ct);
            }
            await using (var admin = await OpenAdminAsync(ct))
            {
                await RequireNoSessionsAsync(admin, name);
                await using var protect = new NpgsqlCommand($"ALTER DATABASE \"{name}\" ALLOW_CONNECTIONS false", admin);
                await protect.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            ready = true;
            Report($"Owned PostgreSQL template: {name}; migrations={migrations}; prepareMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
        catch (Exception error)
        {
            initializationFailure = ExceptionDispatchInfo.Capture(error);
            if (error is PostgresException { SqlState: PostgresErrorCodes.DuplicateDatabase }) created = false;
            if (created)
            {
                try { await DropDatabaseAsync(name); templateName = null; }
                catch (Exception cleanup) { throw new AggregateException(error, cleanup); }
            }
            else templateName = null;
            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenAdminAsync(CancellationToken ct = default)
    {
        var connection = new NpgsqlConnection(adminConnectionString!);
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private static void Report(string message)
    {
        try { Console.WriteLine(message); }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        { /* Diagnostic output must not lose ownership of a created database. */ }
    }

    private static async Task RequireAbsentAsync(NpgsqlConnection admin, string name, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin);
        command.Parameters.AddWithValue("name", name);
        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync(ct))!);
    }

    private static async Task RequireNoSessionsAsync(NpgsqlConnection admin, string name)
    {
        await using var command = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
        command.Parameters.AddWithValue("name", name);
        var started = Stopwatch.GetTimestamp();
        long remaining;
        do
        {
            remaining = (long)(await command.ExecuteScalarAsync())!;
            if (remaining == 0) return;
            await Task.Delay(50);
        } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        Assert.Equal(0L, remaining);
    }

    private async Task DropDatabaseAsync(string name)
    {
        // Names originate only from this owner's generated template or clone allocations.
        if (!name.StartsWith("pp_", StringComparison.Ordinal) || name.Length > 63
            || name.Length < 36 || !Guid.TryParseExact(name[^32..], "N", out _)
            || name[..^32].Any(c => c != '_' && c is not (>= 'a' and <= 'z')))
            throw new InvalidOperationException("Refusing to remove an unrecognized owned database.");
        await using var admin = await OpenAdminAsync();
        await RequireNoSessionsAsync(admin, name);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\"", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private async ValueTask ReleaseAsync(DatabaseLease lease)
    {
        await gate.WaitAsync();
        try
        {
            if (lease.Released) return;
            if (!leases.TryGetValue(lease.Name, out var known) || !ReferenceEquals(known, lease))
                throw new InvalidOperationException("Database lease does not belong to this template owner.");
            await DropDatabaseAsync(lease.Name);
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
            if (leases.Count != 0)
                throw new InvalidOperationException($"Cannot dispose PostgreSQL template while {leases.Count} owned database leases remain.");
            foreach (var name in pendingCleanup.ToArray())
            {
                await DropDatabaseAsync(name);
                pendingCleanup.Remove(name);
            }
            if (templateName is not null)
            {
                await DropDatabaseAsync(templateName);
                templateName = null;
            }
            disposed = true;
        }
        finally { gate.Release(); }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    internal sealed class DatabaseLease(PostgreSqlMigratedTemplate owner, string name, string connectionString) : IAsyncDisposable
    {
        internal string Name { get; } = name;
        internal string ConnectionString { get; } = connectionString;
        internal bool Released { get; set; }
        public ValueTask DisposeAsync() => owner.ReleaseAsync(this);
    }
}
