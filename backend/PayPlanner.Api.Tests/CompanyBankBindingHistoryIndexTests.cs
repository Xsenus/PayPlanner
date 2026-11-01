using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Services.Companies;
using Xunit;
using Cmd = PayPlanner.Api.Tests.CompanyBankAccountBindingCommandTests;

namespace PayPlanner.Api.Tests;

public sealed class CompanyBankBindingHistoryIndexTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HistoryIndexUpgradeAndDownPreserveConfirmationsAndFinancialRows(bool postgres)
    {
        if (!postgres)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
            await VerifyAsync(db, "20261031232958_AddCompanyBankAccountBindings", "20261101000824_IndexCompanyBankBindingHistory");
            return;
        }
        var settings = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES") ?? throw new InvalidOperationException("Existing local PostgreSQL credentials required."))
        { Database = "postgres", Pooling = false };
        Assert.Equal("127.0.0.1", settings.Host); Assert.Equal(5433, settings.Port); Assert.Equal("payplanner_ci", settings.Username);
        await using var admin = new NpgsqlConnection(settings.ConnectionString); await admin.OpenAsync();
        await using (var check = new NpgsqlCommand("SELECT current_setting('server_version_num')::int/10000=17 AND rolcreatedb AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname=current_user", admin))
            Assert.Equal(true, await check.ExecuteScalarAsync());
        var name = "pp_binding_index_" + Guid.NewGuid().ToString("N"); var created = false;
        try
        {
            await using (var absent = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname=@name", admin))
            { absent.Parameters.AddWithValue("name", name); Assert.Equal(0L, await absent.ExecuteScalarAsync()); }
            created = true; Console.WriteLine($"Owned binding-index database: {name}");
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options);
            await VerifyAsync(db, "20261031233007_AddCompanyBankAccountBindingsPostgreSql", "20261101000834_IndexCompanyBankBindingHistoryPostgreSql");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateDatabase) { created = false; throw; }
        finally { if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name); }
    }

    private static async Task VerifyAsync(PaymentContext db, string previous, string migration)
    {
        await db.Database.MigrateAsync(previous);
        var f = await Cmd.SeedAsync(db); var service = new CompanyBankAccountBindingService(db);
        var first = await service.BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
        await service.RevokeAsync(Cmd.Principal(f.Actor), f.Connection, first.BindingId, await Cmd.VersionAsync(db, f), default);
        await service.BindAsync(Cmd.Principal(f.Actor), f.Connection, f.Bank, "local-id", await Cmd.VersionAsync(db, f), default);
        var history = await Cmd.HistoryAsync(db); var bindings = await BindingsAsync(db);
        await db.Database.MigrateAsync();
        Assert.Contains(migration, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync()); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(history, await Cmd.HistoryAsync(db)); Assert.Equal(bindings, await BindingsAsync(db));
        Assert.Equal(2, await CountHistoryIndexesAsync(db));
        await db.Database.MigrateAsync(previous);
        Assert.Equal(0, await CountHistoryIndexesAsync(db));
        Assert.Equal(history, await Cmd.HistoryAsync(db)); Assert.Equal(bindings, await BindingsAsync(db));
        await db.Database.MigrateAsync(); Assert.Equal(2, await CountHistoryIndexesAsync(db));
        Assert.Equal(history, await Cmd.HistoryAsync(db)); Assert.Equal(bindings, await BindingsAsync(db));
    }
    private static async Task<string> BindingsAsync(PaymentContext db)
        => JsonSerializer.Serialize(await db.CompanyBankAccountBindings.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync());
    private static async Task<long> CountHistoryIndexesAsync(PaymentContext db)
    {
        var connection = db.Database.GetDbConnection(); var opened = connection.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = db.Database.IsNpgsql()
                ? "SELECT count(*) FROM pg_indexes WHERE schemaname=current_schema() AND tablename='CompanyBankAccountBindings' AND indexname IN ('IX_CompanyBankBinding_Company_History','IX_CompanyBankBinding_Bank_History') AND indexdef NOT LIKE '% WHERE %'"
                : "SELECT count(*) FROM sqlite_master WHERE type='index' AND tbl_name='CompanyBankAccountBindings' AND name IN ('IX_CompanyBankBinding_Company_History','IX_CompanyBankBinding_Bank_History') AND sql NOT LIKE '% WHERE %'";
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        finally { if (opened) await db.Database.CloseConnectionAsync(); }
    }
}
