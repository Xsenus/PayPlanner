using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCapacityReservationMigrationTests
{
    [Fact]
    public async Task SqliteConstraintUpgradeRetainsPendingReservation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        await db.Database.MigrateAsync("20260929183450_AddStorageCapacityReservations");
        var objectId = Guid.NewGuid();
        // Insert through the historical column list: the current model includes
        // ProductContextJson, which is deliberately absent before this upgrade.
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageCapacityReservations" ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "Status", "Bytes", "ObjectId", "LocalLocator", "CreatedAt", "UpdatedAt")
            VALUES ({"resume-after-upgrade"}, {"local-volume"}, {"test-group"}, {new string('a', 64)}, {StorageCapacityReservationStatuses.Pending}, {7L}, {objectId}, {"owned/pending.bin"}, {now}, {now})
            """);
        db.ChangeTracker.Clear();
        await db.Database.MigrateAsync();
        var preserved = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(objectId, preserved.ObjectId);
        Assert.Equal(7, preserved.Bytes);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, preserved.Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteCleanAndUpgradePreserveExistingDataAndEnforceReservationShape(bool upgrade)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).UsePayPlannerSqliteMigrations().Options;
        await using var db = new PaymentContext(options);
        if (upgrade)
        {
            await db.Database.MigrateAsync("20260928220521_AddTaskFileCaseDocumentLinks");
            db.Clients.Add(new Client { Name = "Existing capacity migration client" });
            await db.SaveChangesAsync();
        }
        await db.Database.MigrateAsync();
        Assert.Contains("20260929183450_AddStorageCapacityReservations", await db.Database.GetAppliedMigrationsAsync());
        Assert.Contains("20260929183746_ProtectStorageCapacityReservations", await db.Database.GetAppliedMigrationsAsync());
        if (upgrade) Assert.Single(await db.Clients.AsNoTracking().Where(c => c.Name == "Existing capacity migration client").ToArrayAsync());
        await AssertReservationInvariantsAsync(db);
        Assert.False(db.Database.HasPendingModelChanges());
        await AssertCaseQueueIndexesAsync(db);
        await AssertRetirementMarkerAsync(db);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostgreSqlCleanAndUpgradePreserveExistingDataAndEnforceReservationShape(bool upgrade)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        var name = "pp_capacity_schema_" + Guid.NewGuid().ToString("N");
        var settings = new NpgsqlConnectionStringBuilder(raw) { Database = "postgres", Pooling = false };
        await using var admin = new NpgsqlConnection(settings.ConnectionString);
        await admin.OpenAsync();
        var created = false;
        try
        {
            await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin))
            { await create.ExecuteNonQueryAsync(); created = true; }
            settings.Database = name;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(settings.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            if (upgrade)
            {
                await db.Database.MigrateAsync("20260929080553_AddTaskUploadDiscoveryPrefixIndexPostgres");
                db.Clients.Add(new Client { Name = "Existing capacity migration client" });
                await db.SaveChangesAsync();
            }
            await db.Database.MigrateAsync();
            Assert.Contains("AddStorageCapacityReservationsPostgres", string.Join(' ', await db.Database.GetAppliedMigrationsAsync()));
            if (upgrade) Assert.Single(await db.Clients.AsNoTracking().Where(c => c.Name == "Existing capacity migration client").ToArrayAsync());
            await AssertReservationInvariantsAsync(db);
            Assert.False(db.Database.HasPendingModelChanges());
            await AssertCaseQueueIndexesAsync(db);
            await AssertRetirementMarkerAsync(db);
            await db.Database.OpenConnectionAsync();
            await using var index = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE tablename = 'StorageCapacityReservations' AND indexname = 'IX_StorageCapacityReservations_PendingIntentPrefix'",
                (NpgsqlConnection)db.Database.GetDbConnection());
            var definition = Assert.IsType<string>(await index.ExecuteScalarAsync());
            Assert.Contains("varchar_pattern_ops", definition);
            Assert.Contains("ProductContextJson", definition);
            Assert.Contains("pending", definition);
        }
        finally
        {
            Assert.Matches("^pp_capacity_schema_[a-f0-9]{32}$", name);
            if (created)
            {
                var until = DateTime.UtcNow.AddSeconds(5);
                while (true)
                {
                    await using var connections = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @name", admin);
                    connections.Parameters.AddWithValue("name", name);
                    if ((long)(await connections.ExecuteScalarAsync())! == 0) break;
                    if (DateTime.UtcNow >= until) throw new InvalidOperationException("Owned migration database still has active connections; preserved for inspection.");
                    await Task.Delay(100);
                }
                await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\"", admin);
                await drop.ExecuteNonQueryAsync();
            }
        }
    }

    private static async Task AssertRetirementMarkerAsync(PaymentContext db)
    {
        var key = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        db.CaseDocumentUploadRetirements.AddRange(
            new CaseDocumentUploadRetirement { CaseId = 42, ActorId = 7, UploadKey = key, StorageObjectId = objectId, RetiredAt = DateTime.UtcNow },
            new CaseDocumentUploadRetirement { CaseId = 42, ActorId = 8, UploadKey = key, StorageObjectId = objectId, RetiredAt = DateTime.UtcNow },
            new CaseDocumentUploadRetirement { CaseId = 43, ActorId = 7, UploadKey = key, StorageObjectId = objectId, RetiredAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(3, await db.CaseDocumentUploadRetirements.AsNoTracking().CountAsync());
        db.CaseDocumentUploadRetirements.Add(new CaseDocumentUploadRetirement
        { CaseId = 42, ActorId = 7, UploadKey = key, StorageObjectId = Guid.NewGuid(), RetiredAt = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Equal(objectId, (await db.CaseDocumentUploadRetirements.AsNoTracking()
            .SingleAsync(item => item.CaseId == 42 && item.ActorId == 7 && item.UploadKey == key)).StorageObjectId);
    }

    private static async Task AssertCaseQueueIndexesAsync(PaymentContext db)
    {
        await db.Database.OpenConnectionAsync();
        foreach (var (name, columns) in new[]
        {
            ("IX_StorageOperations_OperationType_CreatedAt_Id", new[] { "OperationType", "CreatedAt", "Id" }),
            ("IX_StorageOperations_OperationType_Status_CreatedAt_Id", new[] { "OperationType", "Status", "CreatedAt", "Id" }),
        })
        {
            if (db.Database.IsNpgsql())
            {
                await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE tablename = 'StorageOperations' AND indexname = @name",
                    (NpgsqlConnection)db.Database.GetDbConnection());
                command.Parameters.AddWithValue("name", name);
                var definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
                foreach (var column in columns) Assert.Contains($"\"{column}\"", definition);
                Assert.Contains("\"CreatedAt\" DESC", definition);
                Assert.Contains("\"Id\" DESC", definition);
            }
            else
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = $"PRAGMA index_xinfo(\"{name}\")";
                await using var reader = await command.ExecuteReaderAsync();
                var actual = new List<(string Column, bool Descending)>();
                while (await reader.ReadAsync())
                    if (reader.GetInt64(5) == 1) actual.Add((reader.GetString(2), reader.GetInt64(3) == 1));
                Assert.Equal(columns, actual.Select(item => item.Column));
                Assert.All(actual.Take(actual.Count - 2), item => Assert.False(item.Descending));
                Assert.All(actual.TakeLast(2), item => Assert.True(item.Descending));
            }
        }
    }

    private static async Task AssertReservationInvariantsAsync(PaymentContext db)
    {
        var epoch = await db.StorageCapacityEpochs.SingleAsync();
        Assert.Equal(1, epoch.Id);
        Assert.Equal(0, epoch.Version);
        var reservation = new StorageCapacityReservation
        {
            IdempotencyKey = "capacity-test",
            ResourceKey = "local-volume",
            GroupId = "test-group",
            BindingHash = new string('a', 64),
            Bytes = 3,
            ObjectId = Guid.NewGuid(),
            LocalLocator = "owned/test.bin"
        };
        db.StorageCapacityReservations.Add(reservation);
        await db.SaveChangesAsync();
        Assert.Equal(3, (await db.StorageCapacityReservations.AsNoTracking().SingleAsync()).Bytes);
        await db.Database.MigrateAsync();
        Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        db.Entry(reservation).State = EntityState.Detached;
        var invalid = new StorageCapacityReservation
        {
            IdempotencyKey = "capacity-invalid",
            ResourceKey = "local-volume",
            GroupId = "test-group",
            BindingHash = new string('b', 64),
            Bytes = -1,
            ObjectId = Guid.NewGuid(),
            LocalLocator = "owned/invalid.bin"
        };
        db.StorageCapacityReservations.Add(invalid);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.Entry(invalid).State = EntityState.Detached;
        Assert.Single(await db.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
    }
}
