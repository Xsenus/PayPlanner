using System.IO.Compression;
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
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CoalescingCheckpointWriterCombinesCopyCallbacksAndClosesBeforeReturn(bool postgres, bool bytesAtATime)
    {
        var commits = new CheckpointCommitFault();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, commits);
        var bytes = new byte[bytesAtATime ? 33 : StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 1; bytes[^1] = 2;
        var ticket = await ReserveCheckpointAsync(f, bytes.Length);
        commits.Commits = 0;
        Stream? captured = null;
        try
        {
            var result = await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) =>
            {
                captured = stream;
                Assert.False(stream.CanSeek); Assert.False(stream.CanRead); Assert.True(stream.CanWrite);
                Assert.Throws<NotSupportedException>(() => stream.Position = 0);
                Assert.Throws<NotSupportedException>(() => stream.SetLength(0));
                if (bytesAtATime)
                {
                    for (var index = 0; index < bytes.Length; index++) stream.Write(bytes, index, 1);
                    Assert.Equal(1, commits.Commits); // only initial proof until final buffered write
                    Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
                }
                else
                {
                    for (var offset = 0; offset < bytes.Length; offset += 81920)
                        await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(81920, bytes.Length - offset)), ct);
                }
                Assert.Equal(bytes.Length, stream.Length); Assert.Equal(bytes.Length, stream.Position);
            }, default);
            Assert.Equal(bytesAtATime ? 2 : 3, commits.Commits);
            Assert.Equal(bytesAtATime ? 1 : 2, result.Sequence);
            Assert.Equal(bytes.Length, result.PlannedBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), result.PlannedSha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(ticket.AbsolutePath)); // no ShareNone descriptor remains
            Assert.NotNull(captured); Assert.False(captured.CanWrite);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => captured.WriteAsync(new byte[] { 9 }, default).AsTask());
            await f.Stages.SettlePhysicalAsync(ticket, default).WaitAsync(TimeSpan.FromSeconds(10)); // no hold deadlock
            Assert.Equal(StorageCapacityReservationStatuses.Released, await f.Db.StorageCapacityReservations.Select(r => r.Status).SingleAsync());
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoalescingCheckpointWriterWaitsForDetachedWriteBeforeReleasingBuffer(bool postgres)
    {
        var pause = new CoalescingCommitPause();
        await using var f = await WriterProcessFixture.CreateAsync(postgres, pause);
        var bytes = new byte[StorageStageFileReceipt.MaximumBlockBytes + 4];
        bytes[0] = 7; bytes[StorageStageFileReceipt.MaximumBlockBytes - 1] = 8; bytes[^1] = 9;
        var ticket = await ReserveCheckpointAsync(f, bytes.Length);
        Task? detached = null;
        Task<StorageStageFileReceipt>? operation = null;
        try
        {
            operation = f.Stages.WriteCheckpointedFileAsync(ticket, (stream, ct) =>
            {
                pause.Armed = true;
                detached = stream.WriteAsync(bytes, ct).AsTask();
                return Task.CompletedTask;
            }, default);
            await pause.Hit.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(operation.IsCompleted); // owner must wait for the live buffer user even when completion rejects concurrency
            pause.Resume.TrySetResult();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => operation.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Contains("Concurrent", error.Message);
            Assert.NotNull(detached);
            await detached.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(bytes[..StorageStageFileReceipt.MaximumBlockBytes], await File.ReadAllBytesAsync(ticket.AbsolutePath));
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(StorageStageFileReceipt.MaximumBlockBytes, proof!.PlannedBytes);
            Assert.Equal(1, proof.Sequence);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally
        {
            pause.Resume.TrySetResult();
            if (operation is not null) { try { await operation.WaitAsync(TimeSpan.FromSeconds(15)); } catch { } }
            if (detached is not null) { try { await detached.WaitAsync(TimeSpan.FromSeconds(15)); } catch { } }
            await ticket.CloseWriterAsync();
        }
    }

    private sealed class CoalescingCommitPause : DbTransactionInterceptor
    {
        public bool Armed;
        public TaskCompletionSource Hit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Armed = false;
                Hit.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CoalescingCheckpointWriterFinalizesSuccessfulDisposedFacadeAndRealZip(bool postgres, bool zip)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f, 65536);
        var original = new byte[] { 1, 2, 3, 4 };
        try
        {
            var result = await f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) =>
            {
                if (zip)
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                    await using var entry = archive.CreateEntry("original.bin").Open();
                    await entry.WriteAsync(original, ct);
                }
                else { await stream.WriteAsync(original, ct); await stream.DisposeAsync(); }
            }, default);
            var stored = await File.ReadAllBytesAsync(ticket.AbsolutePath);
            Assert.Equal(stored.Length, result.PlannedBytes);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant(), result.PlannedSha256);
            if (zip)
            {
                using var archive = ZipFile.OpenRead(ticket.AbsolutePath);
                await using var content = Assert.Single(archive.Entries).Open();
                using var output = new MemoryStream(); await content.CopyToAsync(output);
                Assert.Equal(original, output.ToArray());
            }
            else Assert.Equal(original, stored);
            var confirmed = await f.Stages.ConfirmPhysicalSizeAsync(ticket, stored.Length, default);
            await f.Stages.SettlePhysicalAsync(confirmed, default).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { await ticket.CloseWriterAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CoalescingCheckpointWriterCallbackFailureOrCancellationNeverFlushesRemainingBuffer(bool postgres, bool cancellation)
    {
        await using var f = await WriterProcessFixture.CreateAsync(postgres);
        var ticket = await ReserveCheckpointAsync(f);
        using var cancelled = new CancellationTokenSource();
        try
        {
            var operation = f.Stages.WriteCheckpointedFileAsync(ticket, async (stream, ct) =>
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3, 4 }, ct);
                await stream.DisposeAsync();
                if (cancellation) { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); }
                throw new IOException("owned callback failed after buffered bytes");
            }, cancelled.Token);
            if (cancellation) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            else Assert.Equal("owned callback failed after buffered bytes", (await Assert.ThrowsAsync<IOException>(() => operation)).Message);
            Assert.Equal(0, new FileInfo(ticket.AbsolutePath).Length);
            var row = await f.Db.StorageCapacityReservations.AsNoTracking().SingleAsync();
            Assert.True(StorageStageFileReceipt.TryParse(row.StageFileReceiptJson, out var proof));
            Assert.Equal(0, proof!.PlannedBytes);
            Assert.Equal(StorageCapacityReservationStatuses.Pending, row.Status);
        }
        finally { await ticket.CloseWriterAsync(); }
    }
}
