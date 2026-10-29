using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CompanyPrintDefaultsPostgreSqlTests
{
    [Fact]
    public async Task PostgreSql_ConcurrentFirstAndExistingWritesRejectStaleEditorsAndAllowReloadAndReset()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES")
            ?? throw new InvalidOperationException("Required isolated PostgreSQL test role is not configured."));
        Assert.Contains(connection.Host, new[] { "127.0.0.1", "localhost", "::1" });
        Assert.False(string.IsNullOrWhiteSpace(connection.Username));
        var role = connection.Username;
        connection.Database = "postgres"; connection.Pooling = false;
        await using var admin = new NpgsqlConnection(connection.ConnectionString); await admin.OpenAsync();
        await using (var roleCheck = new NpgsqlCommand("SELECT rolcreatedb AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await roleCheck.ExecuteScalarAsync());
        var database = "pp_print_defaults_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            await using var first = Context(connection.ConnectionString);
            await using var second = Context(connection.ConnectionString);
            await first.Database.EnsureCreatedAsync();
            foreach (var settingExists in new[] { false, true })
            {
                var id = settingExists ? 8 : 7;
                first.LegalEntities.Add(new LegalEntity { Id = id, ShortName = "Synthetic company" });
                var store = new CompanyDocumentImagePrintDefaultsService(first);
                var other = new CompanyDocumentImagePrintDefaultsService(second);
                if (settingExists) await store.StageAsync(id, CompanyDocumentImagePrintDefaultsTests.Value(1), true);
                await first.SaveChangesAsync();
                var original = (await other.ReadAsync(id)).Version!;
                var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var winner = store.EditAsync(id, original, async () =>
                {
                    admitted.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    await store.StageAsync(id, CompanyDocumentImagePrintDefaultsTests.Value(5), true);
                    await first.SaveChangesAsync(); return true;
                }, default);
                try
                {
                    await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => other.EditAsync(id, original, () => Task.FromResult(false), default));
                }
                finally { release.TrySetResult(); await winner; }
                Assert.Equal(CompanyDocumentImagePrintDefaultsTests.Value(5), (await other.ReadAsync(id)).Value);
                await Assert.ThrowsAsync<SellerSnapshotConflictException>(() => other.EditAsync(id, original, () => Task.FromResult(false), default));
                var fresh = (await other.ReadAsync(id)).Version!;
                await other.EditAsync(id, fresh, async () =>
                {
                    await other.StageAsync(id, null, true); await second.SaveChangesAsync(); return true;
                }, default);
                var reset = await store.ReadAsync(id);
                Assert.Null(reset.Value);
                if (settingExists) Assert.NotEqual(original, reset.Version); else Assert.Equal(original, reset.Version);
            }
            Assert.Equal(2, await first.LegalEntities.CountAsync());
            Assert.Empty(await first.Payments.ToListAsync());
            Assert.Empty(await first.ClientCases.ToListAsync());
        }
        finally
        {
            Assert.Matches("^pp_print_defaults_[a-f0-9]{32}$", database);
            await using var owner = new NpgsqlCommand("SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name", admin);
            owner.Parameters.AddWithValue("name", database);
            Assert.Equal(role, await owner.ExecuteScalarAsync());
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\"", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static PostgreSqlPaymentContext Context(string connection) => new(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
        .UseNpgsql(connection, options => options.MigrationsAssembly(typeof(PaymentContext).Assembly.GetName().Name)).Options);
}
