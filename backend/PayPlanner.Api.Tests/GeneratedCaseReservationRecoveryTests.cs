using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationRecoveryMigrationPreservesIntentAndRejectsActiveLease(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        var controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        f.Local.LosePutAcknowledgement = true; await GenerateAsync(controller, f.Case.Id, "template");
        f.Local.LosePutAcknowledgement = false; f.Db.ChangeTracker.Clear();
        var local = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var original = await f.Db.StorageCapacityReservations.Where(r => r.ResourceKey == local && r.ProductContextJson != null)
            .Select(r => new { r.IdempotencyKey, r.ResourceKey, r.GroupId, r.BindingHash, r.ObjectId, r.LocalLocator, r.Bytes, r.ProductContextJson }).SingleAsync();
        var row = await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local);
        Assert.Equal(new DateTime(2000, 1, 1), row.GeneratedRecoveryNextAttemptAt);
        Assert.Equal(0, row.GeneratedRecoveryAttemptCount); Assert.Null(row.GeneratedRecoveryLeaseToken);
        var migrations = (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var index = Array.FindIndex(migrations, m => m.Contains("AddGeneratedCaseReservationRecovery", StringComparison.Ordinal));
        Assert.True(index > 0);
        // Миграции, добавленные после метки корня стадии, проверяются своими тестами. Здесь они снимаются заранее,
        // чтобы проверка продолжала относиться к защитному откату именно метки корня стадии (SQLite применяет
        // каждый откат отдельной транзакцией, и более новые откаты иначе успели бы выполниться до отказа).
        var stageRootIndex = Array.FindIndex(migrations, m => m.Contains("AddStageRootIdentity", StringComparison.Ordinal));
        Assert.True(stageRootIndex > index);
        if (stageRootIndex < migrations.Length - 1)
        {
            await f.Db.Database.MigrateAsync(migrations[stageRootIndex]);
            migrations = migrations[..(stageRootIndex + 1)];
        }
        var rootBefore = new { row.StageRootIdentity, row.StageWriterMarkerIdentity, row.StageFileReceiptJson };
        // Physical staging uses a separate fenced reservation, not the document intent row.
        var stageProofBefore = await f.Db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.StageRootIdentity != null).OrderBy(r => r.IdempotencyKey).ThenBy(r => r.ResourceKey)
            .Select(r => new { r.IdempotencyKey, r.ResourceKey, r.StageRootIdentity, r.StageWriterMarkerIdentity, r.StageFileReceiptJson, r.Bytes, r.LocalLocator }).ToArrayAsync();
        Assert.NotEmpty(stageProofBefore);
        Assert.All(stageProofBefore, proof => Assert.NotNull(proof.StageRootIdentity));
        var token = Guid.NewGuid(); row.GeneratedRecoveryLeaseToken = token;
        row.GeneratedRecoveryLeaseExpiresAt = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(1), DateTimeKind.Unspecified);
        await f.Db.SaveChangesAsync();
        // The actual latest-chain downgrade must retain the newer durable proof.
        var latestError = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => f.Db.Database.MigrateAsync(migrations[index - 1]));
        if (postgres)
        {
            var pg = Assert.IsType<Npgsql.PostgresException>(latestError);
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, pg.SqlState);
            Assert.Equal("Cannot discard durable stage root identities", pg.MessageText);
        }
        else
        {
            var sqlite = Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(latestError);
            Assert.Equal(19, sqlite.SqliteErrorCode); Assert.Contains("Identities", sqlite.Message);
        }
        Assert.Equal(migrations, (await f.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(token, await f.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local)
            .Select(r => r.GeneratedRecoveryLeaseToken).SingleAsync());
        Assert.Equal(rootBefore, await f.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local)
            .Select(r => new { r.StageRootIdentity, r.StageWriterMarkerIdentity, r.StageFileReceiptJson }).SingleAsync());
        Assert.Equal(stageProofBefore, await f.Db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.StageRootIdentity != null).OrderBy(r => r.IdempotencyKey).ThenBy(r => r.ResourceKey)
            .Select(r => new { r.IdempotencyKey, r.ResourceKey, r.StageRootIdentity, r.StageWriterMarkerIdentity, r.StageFileReceiptJson, r.Bytes, r.LocalLocator }).ToArrayAsync());

        // SQLite's historical Down rebuilds this table using its historical model.
        // Test the original lease guard and data roundtrip in a separate owned DB
        // at that exact schema boundary, never drop new columns from the real intent.
        await using var boundary = await Fixture.CreateAsync(postgres, 1, true);
        Assert.Equal(0, await boundary.Db.StorageCapacityReservations.CountAsync());
        await boundary.Db.Database.MigrateAsync(migrations[index]);
        using var seedCommand = boundary.Db.Database.GetDbConnection().CreateCommand();
        var reservationModel = boundary.Db.Model.FindEntityType(typeof(StorageCapacityReservation))!;
        var createdAt = reservationModel.FindProperty(nameof(StorageCapacityReservation.CreatedAt))!
            .GetRelationalTypeMapping().CreateParameter(seedCommand, "createdAt", row.CreatedAt);
        var updatedAt = reservationModel.FindProperty(nameof(StorageCapacityReservation.UpdatedAt))!
            .GetRelationalTypeMapping().CreateParameter(seedCommand, "updatedAt", row.UpdatedAt);
        var leaseExpiresAt = reservationModel.FindProperty(nameof(StorageCapacityReservation.GeneratedRecoveryLeaseExpiresAt))!
            .GetRelationalTypeMapping().CreateParameter(seedCommand, "leaseExpiresAt", row.GeneratedRecoveryLeaseExpiresAt);
        var originalDates = new { row.CreatedAt, row.UpdatedAt };
        await boundary.Db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "StorageCapacityReservations"
                ("IdempotencyKey", "ResourceKey", "GroupId", "BindingHash", "ObjectId", "LocalLocator", "Bytes", "ProductContextJson", "Status", "CreatedAt", "UpdatedAt", "GeneratedRecoveryLeaseToken", "GeneratedRecoveryLeaseExpiresAt")
            VALUES ({original.IdempotencyKey}, {original.ResourceKey}, {original.GroupId}, {original.BindingHash}, {original.ObjectId}, {original.LocalLocator}, {original.Bytes}, {original.ProductContextJson}, {row.Status}, {createdAt}, {updatedAt}, {token}, {leaseExpiresAt})
            """);
        var boundaryHistory = (await boundary.Db.Database.GetAppliedMigrationsAsync()).ToArray();
        var error = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => boundary.Db.Database.MigrateAsync(migrations[index - 1]));
        if (postgres)
        {
            var pg = Assert.IsType<Npgsql.PostgresException>(error);
            Assert.Equal(Npgsql.PostgresErrorCodes.RaiseException, pg.SqlState);
            Assert.Equal("Cannot downgrade while generated reservation recovery leases are active", pg.MessageText);
        }
        else
        {
            var sqlite = Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(error);
            Assert.Equal(19, sqlite.SqliteErrorCode); Assert.Contains("ActiveLeases", sqlite.Message);
        }
        Assert.Equal(boundaryHistory, (await boundary.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        Assert.Equal(token, await boundary.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local)
            .Select(r => r.GeneratedRecoveryLeaseToken).SingleAsync());
        await boundary.Db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "StorageCapacityReservations" SET "GeneratedRecoveryLeaseToken" = NULL, "GeneratedRecoveryLeaseExpiresAt" = NULL
            WHERE "IdempotencyKey" = {original.IdempotencyKey} AND "ResourceKey" = {original.ResourceKey}
            """);
        await boundary.Db.Database.MigrateAsync(migrations[index - 1]);
        var preserved = await boundary.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.ResourceKey == local && r.ProductContextJson != null)
            .Select(r => new { r.IdempotencyKey, r.ResourceKey, r.GroupId, r.BindingHash, r.ObjectId, r.LocalLocator, r.Bytes, r.ProductContextJson }).SingleAsync();
        Assert.Equal(original, preserved);
        Assert.Equal(originalDates, await boundary.Db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local)
            .Select(r => new { r.CreatedAt, r.UpdatedAt }).SingleAsync());
        await boundary.Db.Database.MigrateAsync(migrations[index]);
        Assert.Equal(boundaryHistory, (await boundary.Db.Database.GetAppliedMigrationsAsync()).ToArray());
        var defaults = await boundary.Db.StorageCapacityReservations.AsNoTracking().Where(r => r.ResourceKey == local && r.ProductContextJson != null)
            .Select(r => new { r.GeneratedRecoveryLeaseToken, r.GeneratedRecoveryAttemptCount, r.GeneratedRecoveryNextAttemptAt }).SingleAsync();
        Assert.Null(defaults.GeneratedRecoveryLeaseToken); Assert.Equal(0, defaults.GeneratedRecoveryAttemptCount);
        Assert.Equal(new DateTime(2000, 1, 1), defaults.GeneratedRecoveryNextAttemptAt);

        row.GeneratedRecoveryLeaseToken = null; row.GeneratedRecoveryLeaseExpiresAt = null;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        await f.Db.Database.MigrateAsync();
        Assert.Empty(await f.Db.Database.GetPendingMigrationsAsync()); Assert.False(f.Db.Database.HasPendingModelChanges());
        row = await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == local);
        Assert.Null(row.GeneratedRecoveryLeaseToken); Assert.Equal(0, row.GeneratedRecoveryAttemptCount);
        Assert.Equal(new DateTime(2000, 1, 1), row.GeneratedRecoveryNextAttemptAt);
        Assert.Equal(rootBefore, new { row.StageRootIdentity, row.StageWriterMarkerIdentity, row.StageFileReceiptJson });
        await f.RunCourtQueueAsync();
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false, "template")]
    [InlineData(true, "template")]
    [InlineData(false, "work-order")]
    [InlineData(true, "work-order")]
    [InlineData(false, "court")]
    [InlineData(true, "court")]
    public async Task GeneratedReservationWorkerRecoversOriginalBytesWithoutAnotherRequest(bool postgres, string kind)
    {
        await using var f = await CreateFixtureAsync(postgres, 1, true);
        var controller = f.Controller();
        controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        f.Local.LosePutAcknowledgement = true;
        await GenerateAsync(controller, f.Case.Id, kind);
        f.Local.LosePutAcknowledgement = false;
        f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var localResource = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var original = await f.Db.StorageCapacityReservations.AsNoTracking()
            .SingleAsync(r => r.ProductContextJson != null && r.ResourceKey == localResource);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, original.Status);
        byte[] originalBytes;
        await using (var stored = await f.Local.OpenReadAsync(original.LocalLocator, default))
        {
            Assert.NotNull(stored.Content);
            using var copy = new MemoryStream(); await stored.Content.CopyToAsync(copy);
            originalBytes = copy.ToArray();
        }
        Assert.Equal(original.Bytes, originalBytes.LongLength);
        var clientCase = await f.Db.ClientCases.SingleAsync();
        clientCase.Title = "Changed after the original file was saved";
        var order = await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync();
        order.Title = "Changed after the original file was saved"; order.Items.Single().Quantity = 100;
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();

        await f.RunCourtQueueAsync();
        var document = Assert.Single(await f.Db.ClientCaseDocuments.AsNoTracking().ToArrayAsync());
        Assert.Equal(original.ObjectId, Assert.Single(await f.Db.StorageObjects.AsNoTracking().ToArrayAsync()).Id);
        var read = await f.Reader.OpenReadAsync(LegacyStorageSources.CaseDocuments, document.Id,
            document.OriginalFileName, document.ContentType, default);
        Assert.Equal(ProductStorageReadStatus.Available, read.Status); Assert.NotNull(read.File);
        await using (var file = read.File!)
        {
            using var copy = new MemoryStream(); await file.Content.CopyToAsync(copy);
            Assert.Equal(originalBytes, copy.ToArray());
        }
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        var auditAction = kind == "court" ? "Created" : "Generated";
        var audit = Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(auditAction, audit.Action); Assert.Equal(document.Id, audit.ObjectId);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        await f.RunCourtQueueAsync();
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(1, f.Local.PutCount);
        Assert.Equal("false", await f.Db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
    }
}
