using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class InternalChatNoReplyPostgreSqlTests
{
    [Fact]
    public async Task ReminderSchedulePersistsOnFreshPostgreSqlDatabase()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }

        var databaseName = "pp_chat_reminder_test_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = "postgres",
            Pooling = false
        };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = admin.CreateCommand())
        {
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
            {
                Database = databaseName,
                Pooling = false
            };
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(testBuilder.ConnectionString)
                .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            await using var db = new PostgreSqlPaymentContext(options, useObjectAuditModel: false);
            await db.Database.MigrateAsync();
            var thread = new InternalChatThread { Kind = InternalChatThreadKinds.Channel, Name = "Тестовый чат" };
            var dueAt = DateTime.UtcNow.AddDays(1);
            db.InternalChatMessages.Add(new InternalChatMessage
            {
                Thread = thread,
                Kind = InternalChatMessageKinds.User,
                Text = "Ожидаю ответа",
                NextNoReplyReminderAt = dueAt
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var stored = await db.InternalChatMessages.SingleAsync();
            Assert.NotNull(stored.NextNoReplyReminderAt);
            Assert.Equal(dueAt, stored.NextNoReplyReminderAt.Value, TimeSpan.FromMilliseconds(1));
        }
        finally
        {
            await PostgreSqlMigratedTemplate.DropOwnedDatabaseAsync(admin, databaseName);
            NpgsqlConnection.ClearAllPools();
        }
    }
}
