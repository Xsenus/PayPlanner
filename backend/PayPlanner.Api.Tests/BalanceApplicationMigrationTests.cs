using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;
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

    private static async Task<BalanceApplicationIdempotencyTests.Fixture> SeedHistoricalReceiptOwnerAsync(PaymentContext db)
    {
        var role = new Role { Name = "historical-balance-v1", Description = "Synthetic historical receipt role" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.RolePermissions.AddRange(new[] { "clients", "legalEntities", "payments" }.Select(section =>
            new RolePermission { RoleId = role.Id, Section = section, CanView = true, CanEdit = true, CanCreate = true, ViewScope = "all" }));
        var actor = new User { Email = "historical-balance-v1@test.local", FullName = "Historical receipt actor", PasswordHash = "unused", RoleId = role.Id, IsActive = true, IsApproved = true, IsEmployee = true };
        var client = new Client { Name = "Historical receipt owner" };
        db.AddRange(actor, client);
        await db.SaveChangesAsync();
        await HistoricalPaymentMigrationSeed.InsertPaymentAsync(db, new Payment
        {
            Id = 2002,
            ClientId = client.Id,
            Amount = 100m,
            PaidAmount = 0m,
            Date = new DateTime(2026, 10, 31),
            PlannedDate = new DateTime(2026, 10, 31),
            Description = "Synthetic target",
            Type = PaymentType.Income,
            Status = PaymentStatus.Pending,
            DocumentKind = DocumentKind.Payment,
            Notes = "",
            SystemNotes = ""
        });
        await HistoricalPaymentMigrationSeed.InsertLedgerAsync(db, new ClientBalanceTransaction
        {
            Id = 3002,
            ClientId = client.Id,
            Amount = 100m,
            Direction = ClientBalanceDirection.Credit,
            Type = ClientBalanceTransactionType.ManualAdjustment,
            Description = "Synthetic credit"
        });
        return new(actor.Id, false, client.Id, 2002);
    }

    private static async Task VerifyAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        int? paymentId = null;
        int? ledgerId = null;
        BalanceApplicationIdempotencyTests.Fixture? originalFixture = null;
        ApplyClientBalanceRequest? originalRequest = null;
        string? originalHash = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync(previous);
            var client = new Client { Name = "Historical synthetic owner" };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
            var payment = new Payment { Id = 2001, Account = "OLD-BALANCE-RECEIPT", ClientId = client.Id, Type = PaymentType.Income, DocumentKind = DocumentKind.Payment, Amount = 123.45m, PaidAmount = 23.45m, Notes = "Preserve history" };
            await HistoricalPaymentMigrationSeed.InsertPaymentAsync(db, payment);
            var ledger = new ClientBalanceTransaction { Id = 3001, ClientId = client.Id, PaymentId = payment.Id, Amount = 67.89m, Type = ClientBalanceTransactionType.ManualAdjustment, Direction = ClientBalanceDirection.Credit, Description = "Preserve old credit", ExternalKey = "historical-synthetic-key" };
            await HistoricalPaymentMigrationSeed.InsertLedgerAsync(db, ledger);
            paymentId = payment.Id; ledgerId = ledger.Id;
            db.ChangeTracker.Clear();
            // Exercise a genuinely old receipt table: its schema has no State column yet.
            await db.Database.MigrateAsync(current);
            originalFixture = await SeedHistoricalReceiptOwnerAsync(db);
            originalRequest = BalanceApplicationIdempotencyTests.Request(originalFixture);
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE "Payments" SET "PaidAmount" = {25m} WHERE "Id" = {originalFixture.PaymentId}"""));
            await HistoricalPaymentMigrationSeed.InsertLedgerAsync(db, new ClientBalanceTransaction
            {
                Id = 3003,
                ClientId = originalFixture.OwnerId,
                PaymentId = originalFixture.PaymentId,
                Type = ClientBalanceTransactionType.CreditApplied,
                Direction = ClientBalanceDirection.Debit,
                Amount = 25m,
                Description = "Preserve old application"
            });
            // Frozen v1 fixture, independent of the current private command builder.
            var command = FormattableString.Invariant($"{{\"Version\":\"balance-apply-v1\",\"ClientId\":{originalFixture.OwnerId},\"LegalEntityId\":null,\"PaymentId\":{originalFixture.PaymentId},\"Amount\":\"25\",\"Description\":\"\",\"SourcePaymentId\":null}}");
            originalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "BalanceApplicationReceipts"
                    ("RequestId", "ActorUserId", "CommandSha256", "CommandVersion", "ClientId", "LegalEntityId", "PaymentId", "AppliedAmount", "Balance", "CreatedAtUtc")
                VALUES ({originalRequest.RequestId!.Value}, {originalFixture.ActorId}, {originalHash}, 'balance-apply-v1', {originalFixture.OwnerId}, NULL, {originalFixture.PaymentId}, {25m}, {75m}, '2026-10-31 00:00:00')
                """);
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
            var oldReceipt = await db.BalanceApplicationReceipts.AsNoTracking().SingleAsync(r => r.RequestId == originalRequest!.RequestId);
            Assert.Equal(BalanceApplicationState.Committed, oldReceipt.State);
            Assert.Equal(originalHash, oldReceipt.CommandSha256);
            var replay = BalanceApplicationIdempotencyTests.Result(await BalanceApplicationIdempotencyTests.ApplyAsync(db, originalFixture!, originalRequest!));
            Assert.True(replay.IsReplay); Assert.Equal(25m, replay.AppliedAmount); Assert.Equal(75m, replay.Balance);
            Assert.Equal(25m, (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == originalFixture!.PaymentId)).PaidAmount);
        }
        var newKey = Guid.NewGuid();
        db.BalanceApplicationReceipts.Add(new BalanceApplicationReceipt { RequestId = newKey, ActorUserId = 11, ClientId = 22, PaymentId = 33, CommandSha256 = new string('a', 64), AppliedAmount = 1.23m, Balance = 4.56m, CreatedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var receipt = Assert.Single(await db.BalanceApplicationReceipts.AsNoTracking().Where(r => r.RequestId == newKey).ToArrayAsync());
        Assert.Equal(1.23m, receipt.AppliedAmount); Assert.Equal(4.56m, receipt.Balance);
        Assert.Equal(BalanceApplicationState.Committed, receipt.State);
        originalFixture ??= await BalanceApplicationIdempotencyTests.SeedAsync(db, false);
        var cancelledRequest = BalanceApplicationIdempotencyTests.Request(originalFixture);
        Assert.Equal("Cancelled", BalanceApplicationCancellationTests.CancelResult(await BalanceApplicationCancellationTests.CancelAsync(db, originalFixture, cancelledRequest.RequestId!.Value)).State);
        Assert.IsType<Microsoft.AspNetCore.Mvc.ConflictObjectResult>(await BalanceApplicationIdempotencyTests.ApplyAsync(db, originalFixture, cancelledRequest));
        Assert.Empty(db.Model.FindEntityType(typeof(BalanceApplicationReceipt))!.GetForeignKeys());
        Assert.Equal("RequestId", Assert.Single(db.Model.FindEntityType(typeof(BalanceApplicationReceipt))!.FindPrimaryKey()!.Properties).Name);
    }
}
