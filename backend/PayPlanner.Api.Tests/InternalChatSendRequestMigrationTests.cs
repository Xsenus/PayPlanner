using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InternalChatSendRequestMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCleanAndUpgradeAllowOldMessagesButRejectDuplicateSendKey(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        if (upgrade) await db.Database.MigrateAsync("20260929183746_ProtectStorageCapacityReservations");
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("20260929223300_AddInternalChatSendRequestId", await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20260929225657_AddInternalChatNotificationRecovery", await db.Database.GetAppliedMigrationsAsync());
        await AssertSendKeyAsync(db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlCleanAndUpgradeAllowOldMessagesButRejectDuplicateSendKey(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_chat_send_migration_" + Guid.NewGuid().ToString("N");
        var adminSettings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(adminSettings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            {
                await command.ExecuteNonQueryAsync();
                created = true;
            }
            var settings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString) { Database = name };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            if (upgrade) await db.Database.MigrateAsync("20260929184130_AddStorageCapacityReservationsPostgres");
            await db.Database.MigrateAsync();
            db.SystemSettings.Add(new SystemSetting { Key = "notifications.email.enabled", Value = "false" });
            await db.SaveChangesAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Contains("20260929223428_AddInternalChatSendRequestIdPostgres", await db.Database.GetAppliedMigrationsAsync());
            Assert.Contains("20260929225710_AddInternalChatNotificationRecoveryPostgres", await db.Database.GetAppliedMigrationsAsync());
            await AssertSendKeyAsync(db);
        }
        finally
        {
            if (created)
            {
                await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
            }
        }
    }

    private static async Task AssertSendKeyAsync(PaymentContext db)
    {
        var user = new User
        {
            Email = "chat-send-migration@example.test",
            FullName = "Автор",
            PasswordHash = "test",
            Role = new Role { Name = "chat-send-migration" },
            IsActive = true,
            IsApproved = true
        };
        var thread = new InternalChatThread { Name = "Проверка повтора", CreatedByUser = user };
        db.InternalChatThreads.Add(thread);
        await db.SaveChangesAsync();
        var key = Guid.NewGuid();
        db.InternalChatMessages.Add(new InternalChatMessage
        { ThreadId = thread.Id, AuthorUserId = user.Id, Text = "Первое", ClientRequestId = key });
        await db.SaveChangesAsync();
        db.InternalChatMessages.Add(new InternalChatMessage
        { ThreadId = thread.Id, AuthorUserId = user.Id, Text = "Повтор", ClientRequestId = key });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.InternalChatMessages.CountAsync());
        Assert.False((await db.InternalChatMessages.AsNoTracking().SingleAsync()).NotificationDispatchPending);
        db.InternalChatMessages.AddRange(
            new InternalChatMessage { ThreadId = thread.Id, AuthorUserId = user.Id, Text = "Старый клиент 1" },
            new InternalChatMessage { ThreadId = thread.Id, AuthorUserId = user.Id, Text = "Старый клиент 2" });
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.InternalChatMessages.CountAsync());
    }
}
