using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RootIdentityMigrationPreservesLegacyIntentAndRejectsDestructiveDowngrade(bool postgres, bool released)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        await using PaymentContext db = postgres
            ? new PostgreSqlPaymentContext(new DbContextOptionsBuilder<PostgreSqlPaymentContext>()
                .UseNpgsql(f.Db.Database.GetConnectionString()).Options)
            : new PaymentContext(new DbContextOptionsBuilder<PaymentContext>()
                .UseSqlite($"Data Source={Path.Combine(f.Root, "root-identity-migrations.sqlite")};Pooling=False").UsePayPlannerSqliteMigrations().Options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        var migrations = db.Database.GetMigrations().ToArray();
        var receiptMigration = Assert.Single(migrations, m => m.Contains("AddStageRootIdentity"));
        var previous = migrations[Array.IndexOf(migrations, receiptMigration) - 1];
        await db.Database.MigrateAsync(previous);
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var status = released ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending;
        const string intent = "{\"legacy\":\"unchanged\"}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageCapacityReservations" ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "Status", "Bytes", "ObjectId", "LocalLocator", "ProductContextJson", "CreatedAt", "UpdatedAt")
            VALUES ({"owned-root-identity-migration"}, {"owned-resource"}, {"owned-group"}, {new string('a', 64)}, {status}, {8L}, {id}, {"owned/legacy.tmp"}, {intent}, {now}, {now})
            """);
        await db.Database.MigrateAsync();
        var row = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Null(row.StageRootIdentity);
        Assert.Equal(intent, row.ProductContextJson);
        Assert.Equal(id, row.ObjectId); Assert.Equal(8, row.Bytes); Assert.Equal(status, row.Status);
        Assert.False(row.StageWriterAdmissionPending);
        const string receipt = "windows-stage-root-v1:0000000000000001:00000000000000000000000000000001:0000000000000001";
        await db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageRootIdentity, receipt));
        await Assert.ThrowsAnyAsync<DbException>(() => db.Database.MigrateAsync(previous));
        Assert.Contains(receiptMigration, await db.Database.GetAppliedMigrationsAsync());
        await db.Database.MigrateAsync(); // Restore a newer empty column after a partial SQLite Down.
        row = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(receipt, row.StageRootIdentity); Assert.Equal(intent, row.ProductContextJson);
        Assert.Equal(id, row.ObjectId); Assert.Equal(8, row.Bytes); Assert.Equal(status, row.Status);
        // Explicitly retire the synthetic test proof; downgrade may now proceed.
        await db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageRootIdentity, (string?)null));
        await db.Database.MigrateAsync(previous);
        await db.Database.MigrateAsync();
        row = await db.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Null(row.StageRootIdentity); Assert.Equal(intent, row.ProductContextJson);
        Assert.Equal(id, row.ObjectId); Assert.Equal(8, row.Bytes); Assert.Equal(status, row.Status);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}
