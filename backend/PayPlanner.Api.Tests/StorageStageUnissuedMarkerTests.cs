using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStagePhysicalSizeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MarkerCreationFollowsCommitAndFailedCreationNeverIssuesWriter(bool postgres, bool blockMarker)
    {
        var order = new MarkerCommitOrder(blockMarker);
        await using var fixture = await CreateFixtureAsync(postgres, order);
        order.MarkerDirectory = Path.Combine(fixture.Root, "signatures", ".stage-writer-fences-v1");
        var path = Path.Combine(fixture.Root, "signatures", "ordered.tmp");
        var admission = await fixture.Stage.ReserveCompatibilityAsync("esign:marker-order", path, 8, "esign-current", default);
        try
        {
            Assert.Equal(1, order.BeforeCommitChecks);
            Assert.Equal(1, order.AfterCommitChecks);
            Assert.False(File.Exists(path));
            Assert.Null(fixture.Db.Database.CurrentTransaction);
            await using var fresh = fixture.NewContext();
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(blockMarker ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(blockMarker ? 2 : 1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            Assert.Equal(8, row.Bytes);
            if (blockMarker)
            {
                Assert.Null(admission.Ticket);
                Assert.Equal("Stage writer fence is unavailable; retry the upload.", admission.Error);
                Assert.Equal(new byte[] { 9, 8, 7 }, await File.ReadAllBytesAsync(order.MarkerDirectory));
            }
            else
            {
                var ticket = Assert.IsType<StorageStageCapacityTicket>(admission.Ticket);
                Assert.Null(admission.Error);
                Assert.Equal(ticket.ObjectId, row.ObjectId);
                var marker = Assert.Single(Directory.GetFiles(order.MarkerDirectory));
                Assert.Equal(64, new FileInfo(marker).Length);
                Assert.Throws<IOException>(() => { using var stream = File.OpenRead(marker); });
            }
            Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
        }
        finally { if (admission.Ticket is not null) await admission.Ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task UnconfirmedPreparationCreatesNoMarkerAndRecoversOnlyDurableUnissuedDebt(bool postgres, bool afterCommit)
    {
        var fault = new CommitFault(afterCommit);
        var unavailable = false;
        var proof = new UnavailableStageReleaseProof(() => unavailable && !fault.Armed);
        await using var fixture = await CreateFixtureAsync(postgres, fault, proof);
        var path = Path.Combine(fixture.Root, "signatures", "unconfirmed.tmp");
        fault.Armed = true;
        unavailable = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Stage.ReserveCompatibilityAsync(
            "esign:marker-unconfirmed", path, 8, "esign-current", default));
        Assert.False(fault.Armed);
        Assert.Equal(1, proof.Interceptions);
        unavailable = false;
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "signatures", ".stage-writer-fences-v1")));
        Assert.False(File.Exists(path));
        Assert.Null(fixture.Db.Database.CurrentTransaction);
        await using var fresh = fixture.NewContext();
        Assert.Equal(afterCommit ? 1 : 0, await fresh.StorageCapacityReservations.CountAsync());
        Assert.Equal(afterCommit ? 1 : 0, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        if (afterCommit)
        {
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(8, row.Bytes);
            var old = DateTime.UtcNow.AddHours(-1);
            await fresh.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, old));
            var recovery = await fixture.Stage.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal("releasedMissingStage", Assert.Single(recovery.Items).Status);
            Assert.Equal(1, recovery.Eligible);
            Assert.Equal(1, recovery.Settled);
            Assert.Equal(StorageCapacityReservationStatuses.Released,
                await fresh.StorageCapacityReservations.Select(item => item.Status).SingleAsync());
            Assert.Equal(2, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        }
        Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task ActivationCommitFailureNeverIssuesWriterAndRetainsDebtUntilFreshProof(bool postgres, bool afterCommit, bool unavailable)
    {
        var fault = new CommitFault(afterCommit, ignoredCommits: 1);
        var blockProof = unavailable;
        var proof = new UnavailableStageReleaseProof(() => blockProof && !fault.Armed);
        await using var fixture = await CreateFixtureAsync(postgres, fault, proof);
        var path = Path.Combine(fixture.Root, "signatures", "activation-ack.tmp");
        fault.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Stage.ReserveCompatibilityAsync(
            "esign:activation-ack", path, 8, "esign-current", default));
        Assert.False(fault.Armed);
        Assert.Equal(unavailable ? 1 : 0, proof.Interceptions);
        blockProof = false;
        Assert.False(File.Exists(path));
        Assert.Null(fixture.Db.Database.CurrentTransaction);
        await using var fresh = fixture.NewContext();
        var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
        Assert.Equal(!afterCommit, row.StageWriterAdmissionPending);
        Assert.Equal(8, row.Bytes);
        Assert.Equal(unavailable ? StorageCapacityReservationStatuses.Pending : StorageCapacityReservationStatuses.Released, row.Status);
        Assert.Equal(unavailable ? 1 : 2, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        var directory = Path.Combine(fixture.Root, "signatures", ".stage-writer-fences-v1");
        var marker = Path.Combine(directory, row.ObjectId.ToString("N") + ".lock");
        Assert.Equal(unavailable, File.Exists(marker));
        if (unavailable)
        {
            Assert.Equal(64, new FileInfo(marker).Length);
            // No ticket escaped. Successful activation persisted native proof;
            // a rolled-back activation has none and must retain unknown debt.
            await using (var exclusive = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            var old = DateTime.UtcNow.AddHours(-1);
            await fresh.StorageCapacityReservations.ExecuteUpdateAsync(setters => setters.SetProperty(item => item.UpdatedAt, old));
            var preview = await fixture.Stage.ReconcilePendingAsync(false, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(afterCommit ? "eligibleMissingStage" : "writerNotProvenStopped", Assert.Single(preview.Items).Status);
            Assert.Equal(0, preview.Settled);
            var recovery = await fixture.Stage.ReconcilePendingAsync(true, 10, TimeSpan.FromMinutes(10), default);
            Assert.Equal(afterCommit ? "releasedMissingStage" : "writerNotProvenStopped", Assert.Single(recovery.Items).Status);
            Assert.Equal(afterCommit ? 1 : 0, recovery.Settled);
            Assert.Equal(afterCommit ? StorageCapacityReservationStatuses.Released : StorageCapacityReservationStatuses.Pending,
                await fresh.StorageCapacityReservations.Select(item => item.Status).SingleAsync());
            Assert.Equal(afterCommit ? 2 : 1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
        }
        Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
        Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
        Assert.False(File.Exists(path));
    }

    private sealed class MarkerCommitOrder(bool blockMarker) : DbTransactionInterceptor
    {
        public string? MarkerDirectory { get; set; }
        public int BeforeCommitChecks { get; private set; }
        public int AfterCommitChecks { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (MarkerDirectory is not null && BeforeCommitChecks == 0)
            {
                Assert.False(Directory.Exists(MarkerDirectory));
                Assert.False(File.Exists(MarkerDirectory));
                BeforeCommitChecks++;
            }
            return ValueTask.FromResult(result);
        }
        public override async Task TransactionCommittedAsync(DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (MarkerDirectory is not null && AfterCommitChecks == 0)
            {
                Assert.False(Directory.Exists(MarkerDirectory));
                Assert.False(File.Exists(MarkerDirectory));
                AfterCommitChecks++;
                if (blockMarker) await File.WriteAllBytesAsync(MarkerDirectory, [9, 8, 7], cancellationToken);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FailedAdmissionNeverCreatesMarkerEvenWhenFreshReceiptProofIsUnavailable(bool postgres, bool proofUnavailable)
    {
        var fault = new ReserveAcknowledgementFault("before");
        var started = false;
        var unavailable = new UnavailableStageReleaseProof(() => proofUnavailable && started && !fault.Armed);
        await using var fixture = await CreateFixtureAsync(postgres, fault, unavailable);
        StorageStageCapacityTicket? winner = null;
        try
        {
            var admission = await fixture.Stage.ReserveCompatibilityAsync("esign:marker-winner",
                Path.Combine(fixture.Root, "signatures", "winner.tmp"), 8, "esign-current", default);
            winner = Assert.IsType<StorageStageCapacityTicket>(admission.Ticket);
            Assert.Null(admission.Error);
            var directory = Path.Combine(fixture.Root, "signatures", ".stage-writer-fences-v1");
            var winnerMarker = Assert.Single(Directory.GetFiles(directory));
            var failedPath = Path.Combine(fixture.Root, "signatures", "unissued.tmp");
            fault.Armed = true;
            started = true;
            await Assert.ThrowsAsync<IOException>(() => fixture.Stage.ReserveCompatibilityAsync(
                "esign:marker-unissued", failedPath, 8, "esign-current", default));
            Assert.False(fault.Armed);
            Assert.Equal(proofUnavailable ? 1 : 0, unavailable.Interceptions);
            Assert.False(File.Exists(failedPath));
            var markers = Directory.GetFiles(directory);
            Assert.Contains(winnerMarker, markers);
            Assert.Single(markers);
            await using var fresh = fixture.NewContext();
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.Equal(winner.ObjectId, row.ObjectId);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
            Assert.Equal(1, await fresh.StorageCapacityEpochs.Select(item => item.Version).SingleAsync());
            Assert.Empty(await fresh.StorageObjects.AsNoTracking().ToArrayAsync());
            Assert.Empty(await fresh.StorageProductBindings.AsNoTracking().ToArrayAsync());
        }
        finally { if (winner is not null) await winner.CloseWriterAsync(); }
    }
}
