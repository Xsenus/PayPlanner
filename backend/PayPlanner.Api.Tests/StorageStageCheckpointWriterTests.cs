using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed partial class StorageStageCapacityServiceTests
{
    private static async Task<StorageStageCapacityTicket> ReserveCheckpointAsync(WriterProcessFixture f, long size = 4)
    {
        Directory.CreateDirectory(Path.Combine(f.Root, "objects"));
        var result = await f.Stages.ReserveAsync("checkpoint:owned", Path.Combine(f.Root, "objects", "checkpoint.tmp"), size, default);
        Assert.Null(result.Error);
        return Assert.IsType<StorageStageCapacityTicket>(result.Ticket);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWriterPersistsBothBoundariesBeforeWriteAndPreservesUnrelatedTrackedData(bool postgres)
    {
        var observer = new CheckpointCommitFault();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, observer);
        var ticket = await ReserveCheckpointAsync(f, StorageStageFileReceipt.MaximumBlockBytes + 4L);
        var client = new Client { Name = "persisted" };
        f.Db.Clients.Add(client); await f.Db.SaveChangesAsync(); client.Name = "unsaved";
        observer.Commits = 0;
        try
        {
            await using (var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default))
            {
                var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
                Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var initial));
                Assert.Equal(0, initial!.Sequence); Assert.Equal(0, initial.PlannedBytes);
                await writer.WriteBlockAsync(new byte[StorageStageFileReceipt.MaximumBlockBytes], default);
                await writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                var complete = await writer.CompleteAsync(default);
                Assert.Equal(2, complete.Sequence);
                Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, complete.PreviousBytes);
                Assert.Equal(ticket.SizeBytes, complete.PlannedBytes);
                Assert.Equal(3, observer.Commits); // initial receipt + two blocks, not per-byte writes
                Assert.Equal(EntityState.Modified, f.Db.Entry(client).State);
                await using var fresh = f.NewContext();
                Assert.Equal("persisted", await fresh.Clients.Where(c => c.Id == client.Id).Select(c => c.Name).SingleAsync());
                Assert.Equal(complete.Serialize(), await fresh.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
                Assert.Equal(StorageCapacityReservationStatuses.Pending, await fresh.StorageCapacityReservations.Select(r => r.Status).SingleAsync());
                Assert.Equal(1, await fresh.StorageCapacityEpochs.Select(r => r.Version).SingleAsync());
            }
            var actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(ticket.AbsolutePath))).ToLowerInvariant();
            var persisted = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(persisted.StageFileReceiptJson, out var proof));
            Assert.Equal(actual, proof!.PlannedSha256);
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointWriterInitialUnknownCommitNeverGrantsUnprovedWriteAuthority(bool postgres, bool afterCommit)
    {
        var fault = new CheckpointCommitFault { AfterCommit = afterCommit };
        await using var f = await WriterProcessFixture.CreateAsync(postgres, fault);
        var ticket = await ReserveCheckpointAsync(f);
        fault.Armed = true;
        try
        {
            if (afterCommit)
            {
                await using var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default);
                await writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default);
                Assert.Equal(4, (await writer.CompleteAsync(default)).PlannedBytes);
            }
            else
            {
                var error = await Assert.ThrowsAsync<IOException>(() => f.Stages.OpenCheckpointWriterAsync(ticket, default));
                Assert.Contains("owned checkpoint", error.Message);
                Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
                Assert.Null(await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
            }
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.Db.StorageCapacityReservations.Select(r => r.Status).SingleAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointWriterBlockUnknownCommitPreservesBoundaryAndSnapshotsCallerMemory(bool postgres, bool afterCommit)
    {
        var fault = new CheckpointCommitFault { AfterCommit = afterCommit };
        await using var f = await WriterProcessFixture.CreateAsync(postgres, fault);
        var ticket = await ReserveCheckpointAsync(f);
        var bytes = new byte[] { 1, 2, 3, 4 };
        try
        {
            await using (var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default))
            {
                fault.Armed = true;
                fault.OnCommitted = () => bytes[0] = 99;
                if (afterCommit)
                {
                    await writer.WriteBlockAsync(bytes, default);
                    var result = await writer.CompleteAsync(default);
                    Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 })).ToLowerInvariant(), result.PlannedSha256);
                    Assert.Equal(99, bytes[0]);
                }
                else
                {
                    await Assert.ThrowsAsync<IOException>(() => writer.WriteBlockAsync(bytes, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteBlockAsync(bytes, default));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(default));
                }
            }
            Assert.Equal(afterCommit ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>(), await File.ReadAllBytesAsync(ticket.AbsolutePath));
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var receipt));
            Assert.Equal(afterCommit ? 4 : 0, receipt!.PlannedBytes);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWriterRejectsForgedAndClosedTicketsBeforeCreatingFile(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        foreach (var forged in new[] { ticket with { BindingHash = new string('b', 64) }, ticket with { ObjectId = Guid.NewGuid() }, ticket with { SizeBytes = 8 } })
            await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.OpenCheckpointWriterAsync(forged, default));
        Assert.False(File.Exists(ticket.AbsolutePath));
        await ticket.CloseWriterAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Stages.OpenCheckpointWriterAsync(ticket, default));
        Assert.False(File.Exists(ticket.AbsolutePath));
        Assert.Null(await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWriterCloseWaitsForDescriptorAndStopsFurtherWrites(bool postgres)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        StorageStageCheckpointWriter? writer = null;
        try
        {
            writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default);
            var close = ticket.CloseWriterAsync().AsTask();
            Assert.False(close.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default));
            await writer.DisposeAsync(); writer = null;
            await close.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(0, proof!.Sequence);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally { if (writer is not null) await writer.DisposeAsync(); await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWriterCancellationAfterCommitRetainsPlannedProofWithoutWriting(bool postgres)
    {
        using var cancellation = new CancellationTokenSource();
        var fault = new CheckpointCommitFault { AfterCommit = true, OnCommitted = cancellation.Cancel };
        await using var f = await WriterProcessFixture.CreateAsync(postgres, fault);
        var ticket = await ReserveCheckpointAsync(f);
        try
        {
            await using (var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default))
            {
                fault.Armed = true;
                await Assert.ThrowsAsync<IOException>(() => writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, cancellation.Token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default));
            }
            Assert.True(cancellation.IsCancellationRequested);
            Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(0, proof!.PreviousBytes); Assert.Equal(4, proof.PlannedBytes);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckpointWriterUnavailableFreshProofPreservesOriginalErrorAndUnwrittenFile(bool postgres)
    {
        var reads = new CheckpointReadFault();
        var fault = new CheckpointCommitFault { AfterCommit = true, OnCommitted = () => reads.Reject = true };
        await using var f = await WriterProcessFixture.CreateAsync(postgres, fault, reads);
        var ticket = await ReserveCheckpointAsync(f);
        try
        {
            await using (var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default))
            {
                fault.Armed = true;
                var error = await Assert.ThrowsAsync<IOException>(() => writer.WriteBlockAsync(new byte[] { 1, 2, 3, 4 }, default));
                Assert.Equal("owned checkpoint lost acknowledgement", error.Message);
                Assert.True(reads.Rejections > 0);
                await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CompleteAsync(default));
            }
            Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
            await using var fresh = f.NewContext();
            var row = await fresh.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(4, proof!.PlannedBytes); Assert.Equal(0, proof.PreviousBytes);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally { reads.Reject = false; await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckpointWriterForeignFileOrChangedReceiptNeverGetsWriteAuthority(bool postgres, bool changedReceipt)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        var foreign = new byte[] { 9, 8, 7, 6 };
        try
        {
            if (!changedReceipt)
            {
                await File.WriteAllBytesAsync(ticket.AbsolutePath, foreign);
                await Assert.ThrowsAsync<IOException>(() => f.Stages.OpenCheckpointWriterAsync(ticket, default));
                Assert.Equal(foreign, await File.ReadAllBytesAsync(ticket.AbsolutePath));
                Assert.Null(await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
            }
            else
            {
                await using (var writer = await f.Stages.OpenCheckpointWriterAsync(ticket, default))
                {
                    await f.Db.StorageCapacityReservations.ExecuteUpdateAsync(s => s.SetProperty(r => r.StageFileReceiptJson, "{broken"));
                    await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteBlockAsync(foreign, default));
                }
                Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
                Assert.Equal("{broken", await f.Db.StorageCapacityReservations.Select(r => r.StageFileReceiptJson).SingleAsync());
            }
            Assert.Equal(StorageCapacityReservationStatuses.Pending, await f.Db.StorageCapacityReservations.Select(r => r.Status).SingleAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    private sealed class CheckpointReadFault : DbCommandInterceptor
    {
        public bool Reject;
        public int Rejections;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Reject && command.CommandText.Contains("StorageCapacityReservations", StringComparison.Ordinal))
            { Rejections++; throw new IOException("owned unavailable checkpoint proof"); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CheckpointCommitFault : DbTransactionInterceptor
    {
        public bool Armed, AfterCommit;
        public int Commits;
        public Action? OnCommitted;
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData data,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed && !AfterCommit) { Armed = false; throw new IOException("owned checkpoint before commit"); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken cancellationToken = default)
        {
            Commits++;
            if (Armed && AfterCommit) { Armed = false; OnCommitted?.Invoke(); throw new IOException("owned checkpoint lost acknowledgement"); }
            return Task.CompletedTask;
        }
    }
}
