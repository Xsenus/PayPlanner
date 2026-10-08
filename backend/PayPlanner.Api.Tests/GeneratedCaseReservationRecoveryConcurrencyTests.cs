using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationRecoveryHonorsBatchLimit(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f); await PrepareReservationAsync(f, "work-order");
        f.FinancialStorageOptions.WorkerBatchSize = 1;
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Db.ChangeTracker.Clear();
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var counts = await f.Db.StorageCapacityReservations.Where(r => r.ProductContextJson != null)
            .OrderBy(r => r.GeneratedRecoveryAttemptCount).Select(r => r.GeneratedRecoveryAttemptCount).ToArrayAsync();
        Assert.Equal(new[] { 0, 1 }, counts);
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Db.ChangeTracker.Clear();
        Assert.Equal(2, await f.Db.ClientCaseDocuments.CountAsync()); Assert.Equal(2, await f.Db.StorageProductBindings.CountAsync());
        Assert.Equal(2, await f.Db.ObjectAuditEvents.CountAsync(a => a.ObjectType == "client-case-document"));
        Assert.Equal(0, await f.ReservationRecovery().ProcessBatchAsync(default)); Assert.Equal(2, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationRecoveryKeepsAcceptedVersionCollisionAsConflict(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f); await PrepareReservationAsync(f);
        f.FinancialStorageOptions.WorkerBatchSize = 1;
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default));
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Db.ChangeTracker.Clear();
        var document = Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Equal(1, document.Version);
        Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var statuses = await f.Db.StorageCapacityReservations.Where(r => r.ProductContextJson != null)
            .OrderBy(r => r.GeneratedRecoveryLastStatus).Select(r => r.GeneratedRecoveryLastStatus).ToArrayAsync();
        Assert.Equal(new[] { "conflict", "uploaded" }, statuses);
        Assert.Equal(2, f.Local.PutCount); Assert.Equal(0, await FinancialRowsAsync(f.Db));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationRecoveryRejectsRetiredAttemptBeforeProviderRead(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f);
        var original = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
        using var intent = System.Text.Json.JsonDocument.Parse(original.ProductContextJson!);
        var key = intent.RootElement.GetProperty("uploadKey").GetGuid();
        Assert.Equal("retired", await new CaseDocumentUploadRetirementService(f.Db).RetireAsync(f.Case.Id, f.User.Id, key, default));
        var reads = f.Local.ReadCount;
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Db.ChangeTracker.Clear();
        Assert.Equal(reads, f.Local.ReadCount); Assert.Equal(1, f.Local.PutCount);
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var after = await f.Db.StorageCapacityReservations.SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(original.ObjectId, after.ObjectId); Assert.Equal(original.ProductContextJson, after.ProductContextJson);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, after.Status); Assert.Equal("retired", after.GeneratedRecoveryLastStatus);
        Assert.Null(after.GeneratedRecoveryLeaseToken); Assert.Single(await f.Db.CaseDocumentUploadRetirements.ToArrayAsync());
    }

    private static async Task PrepareReservationAsync(Fixture f, string kind = "template")
    {
        var controller = f.Controller(); controller.Request.Headers["X-Upload-Key"] = Guid.NewGuid().ToString("D");
        f.Local.LosePutAcknowledgement = true;
        await GenerateAsync(controller, f.Case.Id, kind);
        f.Local.LosePutAcknowledgement = false; f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationConcurrentWorkersClaimOnlyOneAttempt(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f); f.PrepareIdentityObservers();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Local.BeforeReadAsync = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        await using var first = f.IdentityObserver(); await using var second = f.IdentityObserver();
        var run = f.ReservationRecovery(first).ProcessBatchAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Assert.Equal(0, await f.ReservationRecovery(second).ProcessBatchAsync(default));
        }
        finally { release.TrySetResult(); }
        Assert.Equal(1, await run); f.Local.BeforeReadAsync = null;
        await using var proof = f.IdentityObserver();
        var reservation = await proof.StorageCapacityReservations.SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(1, reservation.GeneratedRecoveryAttemptCount); Assert.Null(reservation.GeneratedRecoveryLeaseToken);
        Assert.Equal("uploaded", reservation.GeneratedRecoveryLastStatus);
        Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await proof.StorageObjects.ToArrayAsync());
        Assert.Single(await proof.StorageProductBindings.ToArrayAsync());
        Assert.Single(await proof.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(proof)); Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationCancellationRetainsLeaseUntilExpiryAndThenRecovers(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f);
        f.FinancialStorageOptions.WorkerLeaseSeconds = 15;
        using var cancellation = new CancellationTokenSource();
        f.Local.BeforeReadAsync = ct => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.ReservationRecovery().ProcessBatchAsync(cancellation.Token));
        f.Local.BeforeReadAsync = null; f.Db.ChangeTracker.Clear();
        var original = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(1, original.GeneratedRecoveryAttemptCount); Assert.NotNull(original.GeneratedRecoveryLeaseToken);
        Assert.NotNull(original.GeneratedRecoveryLeaseExpiresAt); Assert.Null(original.GeneratedRecoveryLastStatus);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, original.Status);
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        var reads = f.Local.ReadCount;
        Assert.Equal(0, await f.ReservationRecovery().ProcessBatchAsync(default)); Assert.Equal(reads, f.Local.ReadCount);
        var clock = new AdvancingClock(); clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, await f.ReservationRecovery(clock: clock).ProcessBatchAsync(default)); f.Db.ChangeTracker.Clear();
        var after = await f.Db.StorageCapacityReservations.SingleAsync(r => r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == original.ResourceKey);
        Assert.Equal(original.ObjectId, after.ObjectId); Assert.Equal(2, after.GeneratedRecoveryAttemptCount);
        Assert.Null(after.GeneratedRecoveryLeaseToken); Assert.Equal("uploaded", after.GeneratedRecoveryLastStatus);
        Assert.Single(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Single(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(1, f.Local.PutCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedReservationStaleWorkerCannotPublishOrOverwriteNewExecution(bool postgres)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f); f.FinancialStorageOptions.WorkerLeaseSeconds = 15; f.PrepareIdentityObservers();
        var firstClock = new FixedReservationRecoveryClock(DateTimeOffset.UtcNow);
        var winnerClock = new FixedReservationRecoveryClock(firstClock.GetUtcNow().AddMinutes(5));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        f.Local.BeforeReadAsync = async ct => { if (Interlocked.Increment(ref reads) == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } };
        await using var first = f.IdentityObserver(); await using var second = f.IdentityObserver();
        var run = f.ReservationRecovery(first, firstClock).ProcessBatchAsync(default);
        StorageCapacityReservation? original = null, winner = null;
        Exception? primaryFailure = null;
        var firstClaimed = 0;
        var secondClaimed = 0;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(45));
            await using var observation = f.IdentityObserver();
            original = await observation.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
            secondClaimed = await f.ReservationRecovery(second, winnerClock).ProcessBatchAsync(default);
            winner = await observation.StorageCapacityReservations.AsNoTracking().SingleAsync(r =>
                r.IdempotencyKey == original.IdempotencyKey && r.ResourceKey == original.ResourceKey);
        }
        catch (Exception error) { primaryFailure = error; }
        finally
        {
            release.TrySetResult();
            try { firstClaimed = await run; }
            catch (Exception) when (primaryFailure is not null) { /* Preserve the earlier failure after joining the old execution. */ }
            finally { f.Local.BeforeReadAsync = null; }
        }
        if (primaryFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        Assert.Equal(1, secondClaimed); Assert.Equal(1, firstClaimed);
        Assert.NotNull(original); Assert.NotNull(winner);
        Assert.Equal(1, original.GeneratedRecoveryAttemptCount); Assert.NotNull(original.GeneratedRecoveryLeaseToken);
        Assert.NotNull(original.GeneratedRecoveryLeaseExpiresAt);
        // The old execution still considers its original lease valid; only the later execution may reclaim it.
        Assert.True(original.GeneratedRecoveryLeaseExpiresAt > firstClock.GetUtcNow().UtcDateTime);
        Assert.True(original.GeneratedRecoveryLeaseExpiresAt <= winnerClock.GetUtcNow().UtcDateTime);
        // These values were captured before releasing the old execution.
        Assert.Equal(2, winner.GeneratedRecoveryAttemptCount); Assert.Null(winner.GeneratedRecoveryLeaseToken);
        Assert.Null(winner.GeneratedRecoveryLeaseExpiresAt); Assert.Equal("uploaded", winner.GeneratedRecoveryLastStatus);
        Assert.Equal(original.ObjectId, winner.ObjectId);
        await using var proof = f.IdentityObserver();
        var reservation = await proof.StorageCapacityReservations.SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(winner.ObjectId, reservation.ObjectId);
        Assert.Equal(winner.GeneratedRecoveryNextAttemptAt, reservation.GeneratedRecoveryNextAttemptAt);
        Assert.Equal(winner.GeneratedRecoveryLastStatus, reservation.GeneratedRecoveryLastStatus);
        Assert.Equal(winner.GeneratedRecoveryAttemptCount, reservation.GeneratedRecoveryAttemptCount);
        Assert.Equal(winner.GeneratedRecoveryLeaseToken, reservation.GeneratedRecoveryLeaseToken);
        Assert.Equal(winner.GeneratedRecoveryLeaseExpiresAt, reservation.GeneratedRecoveryLeaseExpiresAt);
        Assert.Equal(2, reservation.GeneratedRecoveryAttemptCount); Assert.Null(reservation.GeneratedRecoveryLeaseToken);
        Assert.Equal("uploaded", reservation.GeneratedRecoveryLastStatus);
        Assert.Single(await proof.ClientCaseDocuments.ToArrayAsync()); Assert.Single(await proof.StorageObjects.ToArrayAsync());
        Assert.Single(await proof.StorageProductBindings.ToArrayAsync());
        Assert.Single(await proof.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        Assert.Equal(0, await FinancialRowsAsync(proof)); Assert.Equal(1, f.Local.PutCount);
    }

    private sealed class FixedReservationRecoveryClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [InlineData(false, "removed-source")]
    [InlineData(true, "removed-source")]
    public async Task GeneratedReservationRechecksAuthorityAfterReadingOriginalBytes(bool postgres, string boundary)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f, "work-order");
        var changed = false;
        f.Local.BeforeReadAsync = async ct =>
        {
            if (changed) return; changed = true;
            if (boundary == "revoked")
                (await f.Db.RolePermissions.SingleAsync(p => p.RoleId == f.User.RoleId && p.Section == RolePermissionCatalog.Cases, ct)).CanEdit = false;
            else f.Db.ClientCaseWorkOrders.Remove(await f.Db.ClientCaseWorkOrders.Include(o => o.Items).SingleAsync(ct));
            await f.Db.SaveChangesAsync(ct); f.Db.ChangeTracker.Clear();
        };
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Local.BeforeReadAsync = null;
        Assert.True(changed); f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync());
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var reservation = await f.Db.StorageCapacityReservations.SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(1, reservation.GeneratedRecoveryAttemptCount); Assert.Null(reservation.GeneratedRecoveryLeaseToken);
        Assert.Equal("forbidden", reservation.GeneratedRecoveryLastStatus); Assert.Equal(StorageCapacityReservationStatuses.Pending, reservation.Status);
        Assert.Equal(0, await FinancialRowsAsync(f.Db)); Assert.Equal(1, f.Local.PutCount);
    }
}
