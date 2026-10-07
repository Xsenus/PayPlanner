using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class CaseDocumentNotificationDispatchMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCleanAndUpgradePreserveExistingNotifications(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20260930073541_AddStorageUploadProductContexts");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlCleanAndUpgradePreserveExistingNotifications(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Owned PostgreSQL dispatch migration requires PAYPLANNER_TEST_POSTGRES.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_case_dispatch_migration_" + Guid.NewGuid().ToString("N");
        Console.WriteLine($"Owned case dispatch migration database: {name}");
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
            await AssertMigrationAsync(db, upgrade, "20260930073631_AddStorageUploadProductContextsPostgres");
        }
        finally
        {
            if (created)
            {
                await using var sessions = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                sessions.Parameters.AddWithValue("name", name);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                long remaining;
                do { remaining = (long)(await sessions.ExecuteScalarAsync())!; if (remaining == 0) break; await Task.Delay(50); }
                while (DateTime.UtcNow < deadline);
                Assert.Equal(0L, remaining);
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task AssertMigrationAsync(PaymentContext db, bool upgrade, string previousMigration)
    {
        UserNotification? old = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync(previousMigration);
            old = new UserNotification
            {
                RecipientUser = new User
                {
                    Email = "case-dispatch-migration@example.local",
                    FullName = "Синтетический сотрудник",
                    PasswordHash = "synthetic",
                    Role = new Role { Name = "owned-case-dispatch-migration" }
                },
                EventKey = "old-case-event",
                Type = "case-document",
                Title = "До обновления",
                BodyPreview = "Синтетическое событие"
            };
            db.UserNotifications.Add(old); await db.SaveChangesAsync();
        }
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(await db.CaseDocumentNotificationDispatches.AsNoTracking().ToListAsync());
        if (old is not null)
        {
            var saved = await db.UserNotifications.AsNoTracking().SingleAsync(n => n.Id == old.Id);
            Assert.Equal("До обновления", saved.Title);
            Assert.Equal("old-case-event", saved.EventKey);
        }
        else
        {
            old = new UserNotification
            {
                RecipientUser = new User
                {
                    Email = "case-dispatch-clean@example.local",
                    FullName = "Синтетический сотрудник",
                    PasswordHash = "synthetic",
                    Role = new Role { Name = "owned-case-dispatch-migration" }
                },
                EventKey = "new-case-event",
                Type = "case-document"
            };
            db.UserNotifications.Add(old); await db.SaveChangesAsync();
        }
        var dispatch = new CaseDocumentNotificationDispatch { UserNotificationId = old.Id, Channel = "realtime" };
        db.CaseDocumentNotificationDispatches.Add(dispatch); await db.SaveChangesAsync();
        Assert.Equal(dispatch.Id, (await db.CaseDocumentNotificationDispatches.AsNoTracking().SingleAsync()).Id);
        db.CaseDocumentNotificationDispatches.Add(new CaseDocumentNotificationDispatch { UserNotificationId = old.Id, Channel = "realtime" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
