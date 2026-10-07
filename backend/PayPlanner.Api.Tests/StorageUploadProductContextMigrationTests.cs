using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageUploadProductContextMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCleanAndUpgradeKeepOldReservationAndPersistExactIntent(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
            .UseSqlite(connection).UsePayPlannerSqliteMigrations().Options);
        await AssertMigrationAsync(db, upgrade, "20260929225657_AddInternalChatNotificationRecovery");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlCleanAndUpgradeKeepOldReservationAndPersistExactIntent(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        Assert.False(string.IsNullOrWhiteSpace(raw), "Owned PostgreSQL context migration requires PAYPLANNER_TEST_POSTGRES.");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        Assert.Contains(settings.Host, new[] { "127.0.0.1", "localhost", "::1" });
        var name = "pp_upload_context_" + Guid.NewGuid().ToString("N");
        Console.WriteLine($"Owned upload context migration database: {name}");
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
            await AssertMigrationAsync(db, upgrade, "20260929225710_AddInternalChatNotificationRecoveryPostgres");
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
        var objectId = Guid.NewGuid();
        if (upgrade)
        {
            await db.Database.MigrateAsync(previousMigration);
            var storedNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            object createdAt = db.Database.IsNpgsql()
                ? new NpgsqlParameter("createdAt", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = storedNow } : storedNow;
            object updatedAt = db.Database.IsNpgsql()
                ? new NpgsqlParameter("updatedAt", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = storedNow } : storedNow;
            // The historical schema has no ProductContextJson column.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageCapacityReservations" ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "Status", "Bytes", "ObjectId", "LocalLocator", "CreatedAt", "UpdatedAt")
                VALUES ({"old-pending-intent"}, {"local-volume"}, {"owned-group"}, {new string('a', 64)}, {"pending"}, {7L}, {objectId}, {"owned/pending.bin"}, {createdAt}, {updatedAt})
                """);
        }
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains("AddStorageUploadProductContexts", string.Join(' ', await db.Database.GetAppliedMigrationsAsync()));
        if (upgrade)
        {
            var old = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(objectId, old.ObjectId);
            Assert.Equal(7, old.Bytes);
            Assert.Equal("pending", old.Status);
            Assert.Equal("owned/pending.bin", old.LocalLocator);
            Assert.Null(old.ProductContextJson);
        }
        const string json = "{\"caseId\":42,\"documentKey\":\"owned-stable-key\",\"version\":2,\"uploadedAt\":\"2026-09-30T00:00:00Z\",\"fileName\":\"Синтетический документ.pdf\"}";
        var reservation = new StorageCapacityReservation
        {
            IdempotencyKey = "new-pending-intent",
            ResourceKey = "local-volume",
            GroupId = "owned-group",
            BindingHash = new string('b', 64),
            Bytes = 8,
            ObjectId = Guid.NewGuid(),
            LocalLocator = "owned/new.bin",
            ProductContextJson = json,
        };
        db.StorageCapacityReservations.Add(reservation);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(json, await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == "new-pending-intent").Select(r => r.ProductContextJson).SingleAsync());
        Assert.Equal(65536, db.Model.FindEntityType(typeof(StorageCapacityReservation))!.FindProperty(nameof(StorageCapacityReservation.ProductContextJson))!.GetMaxLength());
    }
}
