using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparationMigrationDefaultsLegacyRowsToProtectedStateAndGuardsDowngrade(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(f.Db.Database.GetConnectionString()).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={Path.Combine(f.Root, "migrations.sqlite")};Pooling=False").UsePayPlannerSqliteMigrations().Options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var admissionIndex = Array.FindIndex(migrations, name => name.Contains("AddStageWriterAdmission", StringComparison.Ordinal));
        Assert.True(admissionIndex > 0);
        var newest = migrations[admissionIndex];
        var previous = migrations[admissionIndex - 1];
        await db.Database.MigrateAsync(previous);
        var objectId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageCapacityReservations" ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "Status", "Bytes", "ObjectId", "LocalLocator", "CreatedAt", "UpdatedAt")
            VALUES ({"legacy-preparation-migration"}, {"owned-resource"}, {"owned-group"}, {new string('a', 64)}, {StorageCapacityReservationStatuses.Pending}, {4L}, {objectId}, {"owned/legacy.tmp"}, {now}, {now})
            """);
        await db.Database.MigrateAsync();
        var row = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.False(row.StageWriterAdmissionPending);
        Assert.Equal(objectId, row.ObjectId); Assert.Equal(4, row.Bytes);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.False(db.Database.HasPendingModelChanges());
        await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.StageWriterAdmissionPending, true));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.MigrateAsync(previous));
        Assert.Contains(newest, await db.Database.GetAppliedMigrationsAsync());
        Assert.True(await db.StorageCapacityReservations.Select(item => item.StageWriterAdmissionPending).SingleAsync());
        Assert.Equal(StorageCapacityReservationStatuses.Pending, await db.StorageCapacityReservations.Select(item => item.Status).SingleAsync());
        await db.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, StorageCapacityReservationStatuses.Released).SetProperty(item => item.ReleasedAt, now));
        await db.Database.MigrateAsync(previous);
        await db.Database.MigrateAsync();
        var restored = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.False(restored.StageWriterAdmissionPending);
        Assert.Equal(objectId, restored.ObjectId); Assert.Equal(4, restored.Bytes);
        Assert.Equal(StorageCapacityReservationStatuses.Released, restored.Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
