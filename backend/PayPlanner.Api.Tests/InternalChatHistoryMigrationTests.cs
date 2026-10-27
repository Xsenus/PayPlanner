using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InternalChatHistoryMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sqlite_CleanAndUpgradePreserveMessagesAndReadCursor(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20261027005425_AddPaymentCreateAttempts", "20261027014147_AddPersonalChatHistory");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSql_CleanAndUpgradePreserveMessagesAndReadCursor(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Migration verification requires the isolated local PostgreSQL role.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_history_" + Guid.NewGuid().ToString("N");
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
            await AssertMigrationAsync(db, upgrade, "20261027005432_AddPaymentCreateAttempts", "20261027014154_AddPersonalChatHistory");
        }
        finally
        {
            if (created) await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, name);
        }
    }

    private static async Task AssertMigrationAsync(PaymentContext db, bool upgrade, string previous, string current)
    {
        int? messageId = null;
        if (upgrade)
        {
            await db.Database.MigrateAsync();
            var role = new Role { Name = "synthetic-history", Description = "Синтетическая роль" };
            var user = new User { Email = "synthetic-history@example.test", FullName = "Синтетический пользователь", Role = role };
            var thread = new InternalChatThread { Kind = InternalChatThreadKinds.Channel, Name = "Синтетический чат" };
            thread.Members.Add(new InternalChatMember { User = user });
            var message = new InternalChatMessage { Thread = thread, AuthorUser = user, Text = "Сохранённый текст" };
            db.InternalChatMessages.Add(message);
            await db.SaveChangesAsync();
            var member = await db.InternalChatMembers.SingleAsync();
            member.LastReadMessageId = message.Id;
            messageId = message.Id;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.MigrateAsync(previous);
            Assert.DoesNotContain(current, await db.Database.GetAppliedMigrationsAsync());
        }
        await db.Database.MigrateAsync();
        Assert.Contains(current, await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        if (upgrade)
        {
            var member = await db.InternalChatMembers.AsNoTracking().SingleAsync();
            Assert.Equal(messageId, member.LastReadMessageId);
            Assert.Null(member.HistoryHiddenThroughMessageId);
            Assert.Equal(0, member.HistoryVisibilityRevision);
            Assert.Equal("Сохранённый текст", (await db.InternalChatMessages.AsNoTracking().SingleAsync()).Text);
        }
    }
}
