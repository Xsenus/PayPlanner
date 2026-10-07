using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class ManagedGeneratedCaseDocumentTests
{
    [Theory]
    [InlineData(false, "automatic")]
    [InlineData(true, "automatic")]
    [InlineData(false, "writes")]
    [InlineData(true, "writes")]
    [InlineData(false, "transaction")]
    [InlineData(true, "transaction")]
    public async Task GeneratedReservationRecoveryDoesNotReadOrClaimWhenDisabledOrInsideTransaction(bool postgres, string boundary)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f);
        if (boundary == "automatic") f.FinancialStorageOptions.AutoCaseDocumentPublicationEnabled = false;
        if (boundary == "writes") f.FinancialStorageOptions.ProductWriteEnabledClasses = [];
        var reads = f.Local.ReadCount;
        await using var transaction = boundary == "transaction" ? await f.Db.Database.BeginTransactionAsync() : null;
        Assert.Equal(0, await f.ReservationRecovery().ProcessBatchAsync(default));
        Assert.Equal(reads, f.Local.ReadCount); Assert.Equal(1, f.Local.PutCount);
        var reservation = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(0, reservation.GeneratedRecoveryAttemptCount); Assert.Null(reservation.GeneratedRecoveryLeaseToken);
        Assert.Null(reservation.GeneratedRecoveryLastStatus); Assert.Equal(StorageCapacityReservationStatuses.Pending, reservation.Status);
        Assert.Empty(await f.Db.StorageObjects.ToArrayAsync()); Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        if (transaction is not null) await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData(false, "automatic")]
    [InlineData(true, "automatic")]
    [InlineData(false, "writes")]
    [InlineData(true, "writes")]
    public async Task GeneratedReservationRecoveryRechecksFlagsAfterOriginalRead(bool postgres, string boundary)
    {
        await using var f = await Fixture.CreateAsync(postgres, 1, true);
        await PrepareReservationAsync(f);
        var changed = false;
        f.Local.BeforeReadAsync = _ =>
        {
            changed = true;
            if (boundary == "automatic") f.FinancialStorageOptions.AutoCaseDocumentPublicationEnabled = false;
            else f.FinancialStorageOptions.ProductWriteEnabledClasses = [];
            return Task.CompletedTask;
        };
        Assert.Equal(1, await f.ReservationRecovery().ProcessBatchAsync(default)); f.Local.BeforeReadAsync = null;
        Assert.True(changed); f.Db.ChangeTracker.Clear();
        Assert.Empty(await f.Db.ClientCaseDocuments.ToArrayAsync()); Assert.Empty(await f.Db.StorageObjects.ToArrayAsync());
        Assert.Empty(await f.Db.StorageProductBindings.ToArrayAsync()); Assert.Equal(0, await FinancialRowsAsync(f.Db));
        Assert.Empty(await f.Db.ObjectAuditEvents.Where(a => a.ObjectType == "client-case-document").ToArrayAsync());
        var reservation = await f.Db.StorageCapacityReservations.SingleAsync(r => r.ProductContextJson != null);
        Assert.Equal(1, reservation.GeneratedRecoveryAttemptCount); Assert.Null(reservation.GeneratedRecoveryLeaseToken);
        Assert.Equal("forbidden", reservation.GeneratedRecoveryLastStatus); Assert.Equal(StorageCapacityReservationStatuses.Pending, reservation.Status);
        Assert.Equal(1, f.Local.PutCount);
    }
}
