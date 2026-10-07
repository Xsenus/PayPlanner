using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, "disabled")]
    [InlineData(true, "disabled")]
    [InlineData(false, "leased")]
    [InlineData(true, "leased")]
    [InlineData(false, "incomplete-lease")]
    [InlineData(true, "incomplete-lease")]
    [InlineData(false, "invalid")]
    [InlineData(true, "invalid")]
    [InlineData(false, "forbidden")]
    [InlineData(true, "forbidden")]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    public async Task GeneratedReservationWorkerRetainsUnsafeAttemptWithoutRenderingOrWriting(bool postgres, string boundary)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        var controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        f.Local.LosePutAcknowledgement = true; await GenerateAsync(controller, f.Case.Id, "template");
        f.Local.LosePutAcknowledgement = false; f.Db.ChangeTracker.Clear();
        var local = System.Text.Json.JsonSerializer.Serialize(new string?[] { "local", null });
        var row = await f.Db.StorageCapacityReservations.SingleAsync(r => r.ResourceKey == local && r.ProductContextJson != null);
        var key = row.IdempotencyKey; var originalId = row.ObjectId;
        var future = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(1), DateTimeKind.Unspecified);
        future = future.AddTicks(-(future.Ticks % 10)); // PostgreSQL stores microseconds.
        var otherToken = Guid.NewGuid();
        switch (boundary)
        {
            case "disabled": f.FinancialStorageOptions.UploadCapacityChecksEnabled = false; break;
            case "leased": row.GeneratedRecoveryLeaseToken = otherToken; row.GeneratedRecoveryLeaseExpiresAt = future; break;
            case "incomplete-lease": row.GeneratedRecoveryLeaseExpiresAt = future; break;
            case "invalid": row.ProductContextJson = "{\"kind\":\"template\"}"; break;
            case "forbidden": (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases)).CanEdit = false; break;
            case "missing": await f.Local.DeleteAsync(row.LocalLocator, default); break;
        }
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var reads = f.Local.ReadCount;
        await f.RunCourtQueueAsync(); f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(1, f.Local.PutCount); Assert.Equal(0, f.Mirror.PutCount);
        var after = await f.Db.StorageCapacityReservations.SingleAsync(r => r.ResourceKey == local && r.IdempotencyKey == key);
        Assert.Equal(originalId, after.ObjectId); Assert.Equal(StorageCapacityReservationStatuses.Pending, after.Status);
        if (boundary is "disabled" or "leased" or "incomplete-lease")
        {
            Assert.Equal(0, after.GeneratedRecoveryAttemptCount); Assert.Null(after.GeneratedRecoveryLastStatus);
            Assert.Equal(boundary == "leased" ? otherToken : (Guid?)null, after.GeneratedRecoveryLeaseToken);
            Assert.Equal(boundary == "disabled" ? (DateTime?)null : future, after.GeneratedRecoveryLeaseExpiresAt);
        }
        else
        {
            Assert.Equal(1, after.GeneratedRecoveryAttemptCount); Assert.Null(after.GeneratedRecoveryLeaseToken);
            Assert.Null(after.GeneratedRecoveryLeaseExpiresAt);
            Assert.Equal(boundary == "missing" ? "unavailable" : boundary, after.GeneratedRecoveryLastStatus);
            Assert.True(after.GeneratedRecoveryNextAttemptAt > DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
        }
        if (boundary != "missing") Assert.Equal(reads, f.Local.ReadCount);
        await f.RunCourtQueueAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(after.GeneratedRecoveryAttemptCount, await f.Db.StorageCapacityReservations
            .Where(r => r.ResourceKey == local && r.IdempotencyKey == key).Select(r => r.GeneratedRecoveryAttemptCount).SingleAsync());
        Assert.Equal(1, f.Local.PutCount); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Equal("false", await f.Db.SystemSettings.Where(s => s.Key == "notifications.email.enabled").Select(s => s.Value).SingleAsync());
    }
}
