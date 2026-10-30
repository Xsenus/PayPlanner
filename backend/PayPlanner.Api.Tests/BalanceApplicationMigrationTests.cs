using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class BalanceApplicationMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CleanAndUpgradePreserveHistoricalMoney(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await VerifyAsync(db, upgrade, "20261028080000_CashInventoryAccountIdentity", "20261030224434_AddBalanceApplicationReceipts");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CleanAndUpgradePreserveHistoricalMoney(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "An existing isolated local PostgreSQL role is required.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        Assert.Equal("payplanner_ci", settings.Username);
        var name = "pp_balance_receipt_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        await using (var identity = new NpgsqlCommand("SELECT current_setting('server_version_num')::int >= 170000 AND inet_server_port() = 5433 AND NOT rolsuper AND NOT rolcreaterole AND NOT rolreplication AND NOT rolbypassrls FROM pg_roles WHERE rolname = current_user", admin))
            Assert.Equal(true, await identity.ExecuteScalarAsync());
        // Reserve only this unpredictable exact name on the already existing server.
        var created = false;
        try
        {
            await using (var absent = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @name", admin))
            { absent.Parameters.AddWithValue("name", name); Assert.Equal(0L, await absent.ExecuteScalarAsync()); }
            created = true;
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await command.ExecuteNonQueryAsync();
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options);
            await VerifyAsync(db, upgrade, "20261028080010_CashInventoryAccountIdentityPostgreSql", "20261030224445_AddBalanceApplicationReceiptsPostgreSql");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.DuplicateDatabase)
        { created = false; throw; }
        finally
        {
            if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static async Task VerifyAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        int? paymentId = null;
        int? ledgerId = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync(previous);
            var client = new Client { Name = "Historical synthetic owner" };
            var payment = new Payment { Account = "OLD-BALANCE-RECEIPT", Client = client, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 123.45m, PaidAmount = 23.45m, Notes = "Preserve history" };
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            var ledger = new ClientBalanceTransaction { ClientId = client.Id, PaymentId = payment.Id, Amount = 67.89m, Type = ClientBalanceTransactionType.ManualAdjustment, Direction = ClientBalanceDirection.Credit, Description = "Preserve old credit", ExternalKey = "historical-synthetic-key" };
            db.ClientBalanceTransactions.Add(ledger);
            await db.SaveChangesAsync();
            paymentId = payment.Id; ledgerId = ledger.Id;
            db.ChangeTracker.Clear();
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        if (upgrade)
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
            Assert.Equal(123.45m, payment.Amount); Assert.Equal(23.45m, payment.PaidAmount);
            Assert.Equal("Preserve history", payment.Notes);
            var ledger = await db.ClientBalanceTransactions.AsNoTracking().SingleAsync(t => t.Id == ledgerId);
            Assert.Equal(67.89m, ledger.Amount); Assert.Equal(payment.Id, ledger.PaymentId);
            Assert.Equal("Preserve old credit", ledger.Description); Assert.Equal("historical-synthetic-key", ledger.ExternalKey);
            Assert.Equal(payment.ClientId, ledger.ClientId);
        }
        db.BalanceApplicationReceipts.Add(new BalanceApplicationReceipt { RequestId = Guid.NewGuid(), ActorUserId = 11, ClientId = 22, PaymentId = 33, CommandSha256 = new string('a', 64), AppliedAmount = 1.23m, Balance = 4.56m, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var receipt = Assert.Single(await db.BalanceApplicationReceipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(1.23m, receipt.AppliedAmount); Assert.Equal(4.56m, receipt.Balance);
        Assert.Empty(db.Model.FindEntityType(typeof(BalanceApplicationReceipt))!.GetForeignKeys());
        Assert.Equal("RequestId", Assert.Single(db.Model.FindEntityType(typeof(BalanceApplicationReceipt))!.FindPrimaryKey()!.Properties).Name);
    }
}
