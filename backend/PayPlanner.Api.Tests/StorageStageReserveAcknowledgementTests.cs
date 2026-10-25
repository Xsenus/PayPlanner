using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStagePhysicalSizeTests
{
    [Theory]
    [InlineData(false, "before")]
    [InlineData(true, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "after")]
    [InlineData(false, "cancel-after")]
    [InlineData(true, "cancel-after")]
    public async Task FailedStageAdmissionReleasesOnlyItsUndeliveredReceiptWithoutSavingUnrelatedChanges(bool postgres, string outcome)
    {
        var fault = new ReserveAcknowledgementFault(outcome);
        await using var fixture = await CreateFixtureAsync(postgres, fault);
        var path = Path.Combine(fixture.Root, "signatures", "undelivered.tmp");
        Client? unsaved = null;
        fault.AfterCommit = () =>
        {
            unsaved = new Client { Name = "Unsaved after admission" };
            fixture.Db.Clients.Add(unsaved);
            return Task.CompletedTask;
        };
        fault.Armed = true;
        var error = await Record.ExceptionAsync(() => fixture.Stage.ReserveCompatibilityAsync(
            "esign:undelivered", path, 8, "esign-current", CancellationToken.None));
        Assert.NotNull(error);
        if (outcome == "cancel-after") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.False(fault.Armed);
        Assert.False(File.Exists(path));
        Assert.Empty(fixture.Db.ChangeTracker.Entries<StorageCapacityReservation>());
        await using var fresh = fixture.NewContext();
        if (outcome == "before") Assert.Empty(await fresh.StorageCapacityReservations.AsNoTracking().ToArrayAsync());
        else
        {
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Released, row.Status);
            Assert.NotNull(row.ReleasedAt);
            Assert.Equal(8, row.Bytes);
            Assert.Null(row.ProductContextJson);
            Assert.NotNull(unsaved);
            Assert.Equal(EntityState.Added, fixture.Db.Entry(unsaved).State);
            Assert.Empty(await fresh.Clients.AsNoTracking().ToArrayAsync());
        }
        Assert.Equal(outcome == "before" ? 0 : 2,
            await fresh.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
        Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndeliveredStageWithExistingBytesIsRetainedWithoutDeletingOrSettlingThem(bool postgres)
    {
        var fault = new ReserveAcknowledgementFault("after");
        await using var fixture = await CreateFixtureAsync(postgres, fault);
        var path = Path.Combine(fixture.Root, "signatures", "foreign.tmp");
        var bytes = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2 };
        fault.AfterCommit = () => File.WriteAllBytesAsync(path, bytes);
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Stage.ReserveCompatibilityAsync(
            "esign:foreign-undelivered", path, 8, "esign-current", CancellationToken.None));
        await using var fresh = fixture.NewContext();
        Assert.Equal(StorageCapacityReservationStatuses.Pending,
            (await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync()).Status);
        Assert.Equal(1, await fresh.StorageCapacityEpochs.Select(row => row.Version).SingleAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentStageAdmissionCannotRetireTheDeliveredWinnerEvenBeforeItsFirstByte(bool postgres)
    {
        await using var fixture = await CreateFixtureAsync(postgres);
        var path = Path.Combine(fixture.Root, "signatures", "active.tmp");
        var (winner, error) = await fixture.Stage.ReserveCompatibilityAsync(
            "esign:active", path, 8, "esign-current", CancellationToken.None);
        Assert.NotNull(winner);
        Assert.Null(error);
        await using var contender = fixture.NewContext();
        var failed = await fixture.CreateStage(contender).ReserveCompatibilityAsync(
            "esign:active", path, 8, "esign-current", CancellationToken.None);
        Assert.Null(failed.Ticket);
        Assert.NotNull(failed.Error);
        await using var fresh = fixture.NewContext();
        var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(winner.ObjectId, row.ObjectId);
        Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        Assert.Equal(1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        await File.WriteAllBytesAsync(path, new byte[8]);
        await fixture.Stage.SettlePhysicalAsync(winner, CancellationToken.None);
    }

    private sealed class ReserveAcknowledgementFault(string outcome) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public Func<Task>? AfterCommit { get; set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && outcome == "before")
            {
                Armed = false;
                throw new IOException("Owned stage admission before commit failure.");
            }
            return ValueTask.FromResult(result);
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (!Armed) return;
            Armed = false;
            if (AfterCommit is not null) await AfterCommit();
            if (outcome == "cancel-after") throw new OperationCanceledException(new CancellationToken(true));
            throw new IOException("Owned stage admission lost acknowledgement.");
        }
    }
}
