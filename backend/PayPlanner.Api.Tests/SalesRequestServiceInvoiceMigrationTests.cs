using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class SalesRequestServiceInvoiceMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CleanAndUpgradePreserveUnlinkedRequest(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20261027014147_AddPersonalChatHistory", "20261027130000_AddSalesRequestServiceInvoice");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CleanAndUpgradePreserveUnlinkedRequest(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Migration verification requires the isolated local PostgreSQL role.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_request_link_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            {
                await command.ExecuteNonQueryAsync();
                created = true;
            }
            settings.Database = name;
            await using var db = new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(settings.ConnectionString).Options);
            await AssertMigrationAsync(db, upgrade, "20261027014154_AddPersonalChatHistory", "20261027130010_AddSalesRequestServiceInvoice");
        }
        finally
        {
            if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static async Task AssertMigrationAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        int? requestId = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync();
            var request = new SalesRequest
            {
                Source = "Call",
                ClientId = 101,
                ClientName = "Синтетический клиент",
                ClientPhone = "test",
                Type = "New",
                Summary = "Сохранённая заявка",
                StatusId = 1,
                ResponsibleUserId = 1,
                CreatedByUserId = 1,
                Version = 3,
            };
            db.SalesRequests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.Id;
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync(previous);
            Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        var one = new SalesRequest { Source = "Call", ClientId = 101, ClientName = "Клиент", ClientPhone = "test", Type = "New", Summary = "Один", StatusId = 1, ResponsibleUserId = 1, CreatedByUserId = 1 };
        var two = new SalesRequest { Source = "Call", ClientId = 101, ClientName = "Клиент", ClientPhone = "test", Type = "New", Summary = "Два", StatusId = 1, ResponsibleUserId = 1, CreatedByUserId = 1 };
        db.SalesRequests.AddRange(one, two);
        await db.SaveChangesAsync();
        one.ServiceInvoiceId = 123456;
        one.ServiceInvoiceAllocationAmount = 40m;
        await db.SaveChangesAsync();
        two.ServiceInvoiceId = 123456;
        two.ServiceInvoiceAllocationAmount = 60m;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(123456, (await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == one.Id)).ServiceInvoiceId);
        var saved = await db.SalesRequests.AsNoTracking().SingleAsync(item => item.Id == two.Id);
        Assert.Equal(123456, saved.ServiceInvoiceId);
        Assert.Equal(60m, saved.ServiceInvoiceAllocationAmount);
        if (upgrade)
        {
            var request = await db.SalesRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
            Assert.Equal("Сохранённая заявка", request.Summary);
            Assert.Equal(3, request.Version);
            Assert.Null(request.ServiceInvoiceId);
            Assert.Null(request.ServiceInvoiceAllocationAmount);
        }
    }
}
