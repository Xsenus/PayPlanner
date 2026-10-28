using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PaymentCreateAttemptMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CleanAndUpgradePreservePayments(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20261024110000_AddDocumentSellerSnapshotPolicy", "20261027005425_AddPaymentCreateAttempts");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CleanAndUpgradePreservePayments(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Migration verification requires the isolated local PostgreSQL role.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_payment_attempt_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await AssertMigrationAsync(db, upgrade, "20261024110001_AddDocumentSellerSnapshotPolicyPostgres", "20261027005432_AddPaymentCreateAttempts");
        }
        finally
        {
            if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static async Task AssertMigrationAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        if (upgrade)
        {
            await db.Database.MigrateAsync(previous);
            db.Payments.Add(new Payment
            {
                Account = "SYNTHETIC-OLD",
                Type = PaymentType.Income,
                DocumentKind = DocumentKind.Invoice,
                Amount = 123.45m
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        if (upgrade)
        {
            var payment = await db.Payments.AsNoTracking().SingleAsync(p => p.Account == "SYNTHETIC-OLD");
            Assert.Equal(123.45m, payment.Amount);
            Assert.Equal(PaymentType.Income, payment.Type);
            Assert.Equal(DocumentKind.Invoice, payment.DocumentKind);
        }
        var now = DateTimeOffset.UtcNow;
        db.PaymentCreateAttempts.Add(new PaymentCreateAttempt
        {
            Id = Guid.NewGuid(),
            ActorUserId = 99,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24)
        });
        await db.SaveChangesAsync();
        Assert.Single(await db.PaymentCreateAttempts.AsNoTracking().ToListAsync());
    }
}
