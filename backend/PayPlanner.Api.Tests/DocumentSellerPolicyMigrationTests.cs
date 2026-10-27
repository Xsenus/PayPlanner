using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DocumentSellerPolicyMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CleanAndUpgradeKeepHistoricalSellerBytes(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20261016132312_AddMoneyBalanceScope", "20261024110000_AddDocumentSellerSnapshotPolicy");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CleanAndUpgradeKeepHistoricalSellerBytes(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Seller migration verification requires the isolated local PostgreSQL role.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_seller_policy_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await command.ExecuteNonQueryAsync(); created = true; }
            Console.WriteLine($"Owned seller-policy migration database: {name}");
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await AssertMigrationAsync(db, upgrade, "20261016132320_AddMoneyBalanceScopePostgreSql", "20261024110001_AddDocumentSellerSnapshotPolicyPostgres");
        }
        finally
        {
            if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static async Task AssertMigrationAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        const string json = "{\"Id\":71,\"ShortName\":\"Историческое имя\",\"ImagesFrozen\":true,\"LogoVersionId\":17}";
        if (upgrade)
        {
            // Seed through the actual latest model, then remove only the new nullable
            // columns. This exercises both Down and the real previous-schema upgrade
            // without teaching EF's current model to write a missing policy column.
            await db.Database.MigrateAsync();
            db.Payments.Add(new Payment
            {
                Account = "OLD-INVOICE",
                Type = PaymentType.Income,
                DocumentKind = DocumentKind.Invoice,
                Amount = 123.45m,
                SellerCompanyId = 71,
                SellerSnapshotJson = json
            });
            db.Acts.Add(new Act { Number = "OLD-ACT", Amount = 123.45m, SellerCompanyId = 71, SellerSnapshotJson = json });
            db.Contracts.Add(new Contract { Number = "OLD-CONTRACT", Amount = 123.45m, SellerCompanyId = 71, SellerSnapshotJson = json });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync(previous);
            Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        foreach (var type in new[] { typeof(Payment), typeof(Act), typeof(Contract) })
            Assert.True(db.Model.FindEntityType(type)!.FindProperty("SellerSnapshotPolicy")!.IsNullable);
        if (upgrade)
        {
            var invoice = await db.Payments.AsNoTracking().SingleAsync(p => p.Account == "OLD-INVOICE");
            var act = await db.Acts.AsNoTracking().SingleAsync(a => a.Number == "OLD-ACT");
            var contract = await db.Contracts.AsNoTracking().SingleAsync(c => c.Number == "OLD-CONTRACT");
            foreach (var seller in new IDocumentSeller[] { invoice, act, contract })
            { Assert.Null(seller.SellerSnapshotPolicy); Assert.Equal(json, seller.SellerSnapshotJson); Assert.Equal(71, seller.SellerCompanyId); }
            Assert.Equal(123.45m, invoice.Amount); Assert.Equal(123.45m, act.Amount); Assert.Equal(123.45m, contract.Amount);
        }
        Assert.False(await db.SystemSettings.AnyAsync(row => row.Key == DocumentSellerSnapshotPolicy.SettingKey));
        Assert.Empty(await db.CompanyBrandImageVersions.ToArrayAsync());
    }
}
