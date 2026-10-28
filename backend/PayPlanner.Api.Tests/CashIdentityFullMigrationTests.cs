using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using Xunit;

namespace PayPlanner.Api.Tests;

public class CashIdentityFullMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_FullMigrationChainPreservesLegacyCashIdentity(bool upgrade)
    {
        var configured = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        var password = Environment.GetEnvironmentVariable("PAYPLANNER_LOCAL_TEST_PG_PASSWORD");
        var builder = !string.IsNullOrWhiteSpace(configured) ? new NpgsqlConnectionStringBuilder(configured)
            : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 5433, Username = "payplanner_ci", Password = password };
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") || builder.Username != "payplanner_ci" || string.IsNullOrWhiteSpace(builder.Password))
            throw new InvalidOperationException("A loopback PostgreSQL endpoint with the isolated payplanner_ci role is required.");
        builder.Database = "postgres";
        builder.Pooling = false;
        var databaseName = "pp_cash_identity_" + Guid.NewGuid().ToString("N");
        Assert.Matches("^pp_cash_identity_[a-f0-9]{32}$", databaseName);
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin)) await create.ExecuteNonQueryAsync();
            created = true;
            builder.Database = databaseName;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(builder.ConnectionString, npgsql => npgsql.MigrationsAssembly("PayPlanner.Api.Migrations")).Options;
            await using var context = new PostgreSqlPaymentContext(options);
            await VerifyAsync(context, upgrade, "20261028070010_BindApprovalNotificationStage", "20261028080010_CashInventoryAccountIdentityPostgreSql");
        }
        finally
        {
            if (created)
            {
                Assert.Matches("^pp_cash_identity_[a-f0-9]{32}$", databaseName);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_FullMigrationChainPreservesLegacyCashIdentity(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var context = new PaymentContext(options);
        await VerifyAsync(context, upgrade, "20261028070000_BindApprovalNotificationStage", "20261028080000_CashInventoryAccountIdentity");
    }

    private static async Task VerifyAsync(PaymentContext context, bool upgrade, string previous, string target)
    {
        if (upgrade)
        {
            await context.Database.MigrateAsync(previous);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO "CashOrders" ("Number", "Type", "Status", "Date", "Amount", "Currency", "Account")
                VALUES ('SYN-LEGACY', 'Incoming', 'Posted', '2026-10-28', 123.45, 'RUB', 'Прежняя касса');
                INSERT INTO "CashInventories" ("Number", "Date", "AccountName", "AccountingBalance", "ActualBalance", "Difference", "CorrectionCashOrderId")
                SELECT 'SYN-INVENTORY', '2026-10-28', 'Прежняя касса', 123.45, 123.45, 0, "Id" FROM "CashOrders" WHERE "Number" = 'SYN-LEGACY';
                """);
        }
        await context.Database.MigrateAsync();
        Assert.Contains(target, await context.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        if (!upgrade)
        {
            Assert.Empty(await context.CashOrders.ToListAsync());
            Assert.Empty(await context.CashInventories.ToListAsync());
            return;
        }
        var order = Assert.Single(await context.CashOrders.ToListAsync());
        var inventory = Assert.Single(await context.CashInventories.ToListAsync());
        Assert.Equal(123.45m, order.Amount);
        Assert.Equal(123.45m, inventory.ActualBalance);
        Assert.Equal(order.Id, inventory.CorrectionCashOrderId);
        Assert.Null(order.MoneyAccountId);
        Assert.Null(order.CompanyId);
        Assert.Null(inventory.MoneyAccountId);
        Assert.Null(inventory.CompanyId);
    }
}
