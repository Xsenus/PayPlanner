using System.Data;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class StorageStageCapacityService
{
    public async Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
        Func<Stream, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        return await WriteCheckpointedFileAsync(ticket, (stream, _, token) => write(stream, token), ct).ConfigureAwait(false);
    }

    public async Task<StorageStageFileReceipt> WriteCheckpointedFileAsync(StorageStageCapacityTicket ticket,
        Func<Stream, StorageStageFileReceipt, CancellationToken, Task> write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        await using var writer = await OpenCheckpointWriterAsync(ticket, ct).ConfigureAwait(false);
        var stream = new StorageStageCoalescingWriteStream(writer, ticket.SizeBytes);
        try
        {
            await write(stream, writer.InitialReceipt, ct).ConfigureAwait(false);
            return await stream.CompleteOwnedAsync(ct).ConfigureAwait(false);
        }
        finally { await stream.ReleaseOwnedBufferAsync().ConfigureAwait(false); }
        // await using closes the descriptor/hold BEFORE this Task returns, so
        // callers can read/settle the physical file without a lifetime deadlock.
    }

    public async Task<StorageStageCheckpointWriter> OpenCheckpointWriterAsync(StorageStageCapacityTicket ticket, CancellationToken ct)
    {
        var fence = ticket.WriterFence ?? throw new InvalidOperationException("An issued writer fence is required.");
        var hold = await fence.HoldWriterAsync(ticket, ct).ConfigureAwait(false);
        FileStream? file = null;
        try
        {
            await RequireCheckpointAuthorityAsync(ticket, null, 0, ct).ConfigureAwait(false);
            var fileOptions = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            file = new(ticket.AbsolutePath, fileOptions);
            var owned = file;
            var observation = RequireDescriptor();
            var initial = StorageStageFileReceipt.Create(ticket.ObjectId, ticket.BindingHash, ticket.SizeBytes, observation.FileIdentity);
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Flush(flushToDisk: true);
            await SaveAsync(null, initial, ct).ConfigureAwait(false);
            var writer = new StorageStageCheckpointWriter(file, hold, initial, SaveAsync, VerifyAsync);
            file = null;
            return writer;

            StorageStageFileObservation RequireDescriptor()
            {
                fence.RequireWriter(ticket);
                var observed = StorageStageFileObservation.Observe(owned.SafeFileHandle);
                if (observed is null || observed.GroupId != ticket.GroupId
                    || !string.Equals(observed.AbsolutePath, Path.GetFullPath(ticket.AbsolutePath),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Stage descriptor path or physical group changed.");
                return observed;
            }

            async Task VerifyAsync(StorageStageFileReceipt receipt, CancellationToken token)
            {
                if (RequireDescriptor().FileIdentity != receipt.FileIdentity) throw new InvalidOperationException("Stage descriptor identity changed.");
                var physicalBytes = owned.Length;
                await RequireCheckpointAuthorityAsync(ticket, receipt.Serialize(), physicalBytes, token,
                    requireRemaining: physicalBytes < receipt.PlannedBytes).ConfigureAwait(false);
                if (RequireDescriptor().FileIdentity != receipt.FileIdentity) throw new InvalidOperationException("Stage descriptor identity changed.");
            }

            async Task SaveAsync(StorageStageFileReceipt? previous, StorageStageFileReceipt planned, CancellationToken token)
            {
                var expected = previous?.Serialize();
                var json = planned.Serialize();
                var started = false;
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
                    await RequireCheckpointAuthorityAsync(ticket, expected, owned.Length, token).ConfigureAwait(false);
                    if (RequireDescriptor().FileIdentity != observation.FileIdentity || owned.Length != (previous?.PlannedBytes ?? 0))
                        throw new InvalidOperationException("Stage boundary changed before checkpoint.");
                    var updatedAt = DateTime.UtcNow;
                    var changed = await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == ticket.Key
                        && r.ResourceKey == ticket.ResourceKey && r.ObjectId == ticket.ObjectId && r.GroupId == ticket.GroupId
                        && r.BindingHash == ticket.BindingHash && r.Bytes == ticket.SizeBytes && r.LocalLocator == ticket.RelativePath
                        && r.StageWriterMarkerIdentity == ticket.WriterMarkerIdentity && r.StageRootIdentity == ticket.RootIdentity
                        && r.Status == StorageCapacityReservationStatuses.Pending && !r.StageWriterAdmissionPending
                        && r.ProductContextJson == null && r.StageFileReceiptJson == expected)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.StageFileReceiptJson, json).SetProperty(r => r.UpdatedAt, updatedAt), token).ConfigureAwait(false);
                    if (changed != 1) throw new InvalidOperationException("Stage checkpoint authority changed.");
                    if (RequireDescriptor().FileIdentity != observation.FileIdentity) throw new InvalidOperationException("Stage descriptor changed during checkpoint.");
                    started = true;
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                }
                catch (Exception) when (started && !token.IsCancellationRequested)
                {
                    // The transaction is disposed. A new read must confirm the
                    // SAME complete state; a timeout never grants write authority.
                    try { await VerifyAsync(planned, token).ConfigureAwait(false); return; }
                    catch (Exception) when (!token.IsCancellationRequested) { }
                    throw;
                }
            }
        }
        catch
        {
            try { if (file is not null) await file.DisposeAsync().ConfigureAwait(false); }
            finally { hold.Dispose(); }
            // Retain both unknown physical bytes and pending durable debt.
            throw;
        }
    }

    private async Task RequireCheckpointAuthorityAsync(StorageStageCapacityTicket ticket, string? receipt,
        long physicalBytes, CancellationToken ct, bool requireRemaining = true)
    {
        var configured = options.CurrentValue;
        var minimumFreeBytes = configured.UploadMinimumFreeBytes;
        if (!configured.UploadCapacityChecksEnabled || minimumFreeBytes < 0)
            throw new InvalidOperationException("Stage capacity policy changed.");
        if (physicalBytes < 0 || physicalBytes > ticket.SizeBytes)
            throw new InvalidOperationException("Stage checkpoint physical boundary changed.");
        var row = await db.StorageCapacityReservations.AsNoTracking().SingleOrDefaultAsync(r => r.IdempotencyKey == ticket.Key
            && r.ResourceKey == ticket.ResourceKey, ct).ConfigureAwait(false);
        if (row is null || row.ObjectId != ticket.ObjectId || row.GroupId != ticket.GroupId || row.BindingHash != ticket.BindingHash
            || row.Bytes != ticket.SizeBytes || row.LocalLocator != ticket.RelativePath || row.StageWriterAdmissionPending
            || row.StageWriterMarkerIdentity != ticket.WriterMarkerIdentity || row.StageRootIdentity != ticket.RootIdentity
            || row.Status != StorageCapacityReservationStatuses.Pending || row.ProductContextJson is not null
            || row.StageFileReceiptJson != receipt || !TryResolvePendingPath(row, out var path, out var root)
            || !string.Equals(path, Path.GetFullPath(ticket.AbsolutePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Stage checkpoint reservation changed.");
        var physical = LocalStorageCapacityProbe.Observe(root, ticket.ResourceKey, minimumFreeBytes);
        if (physical.GroupId != ticket.GroupId || physical.AvailableBytes is null) throw new InvalidOperationException("Stage checkpoint root changed.");
        // Already-written bytes are present in the disk observation. Planning
        // needs the unused admission; completion only reads a finished boundary
        // and must not charge an unused upper estimate as future output.
        var remainingBytes = requireRemaining ? ticket.SizeBytes - physicalBytes : 0;
        if (physical.AvailableBytes.Value < minimumFreeBytes
            || remainingBytes > physical.AvailableBytes.Value - minimumFreeBytes
            || !options.CurrentValue.UploadCapacityChecksEnabled
            || options.CurrentValue.UploadMinimumFreeBytes != minimumFreeBytes)
            throw new InvalidOperationException("Stage checkpoint free capacity or minimum-free policy changed.");
        if (requireRemaining)
        {
            // The existing Status/ResourceKey index excludes released history.
            // Aggregate in SQL: never materialize the pending reservation list.
            // Other writers remain charged at their full admitted size; only
            // this held descriptor's known prefix is already in disk-free.
            var debt = await db.StorageCapacityReservations.AsNoTracking()
                .TagWith("storage-stage-pending-reservation-budget")
                .Where(r => r.Status == StorageCapacityReservationStatuses.Pending && r.GroupId == ticket.GroupId)
                .GroupBy(r => r.GroupId)
                .Select(g => new
                {
                    Invalid = g.LongCount(r => r.Bytes < 0 || r.IdempotencyKey == "" || r.ResourceKey == ""
                        || r.BindingHash == "" || r.LocalLocator == ""),
                    Bytes = g.Sum(r => (decimal?)r.Bytes)
                }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (debt is null || debt.Invalid != 0 || debt.Bytes is null
                || debt.Bytes < ticket.SizeBytes || debt.Bytes > long.MaxValue)
                throw new InvalidOperationException("Stage pending reservation debt exceeds fresh physical headroom.");
            var copies = await ReadCheckpointCopyBudgetAsync(ticket, minimumFreeBytes,
                physical.AvailableBytes.Value, ct).ConfigureAwait(false);
            var remainingDebt = debt.Bytes.Value - physicalBytes + copies.Bytes;
            if (remainingDebt > long.MaxValue || copies.AvailableBytes < minimumFreeBytes
                || remainingDebt > copies.AvailableBytes - minimumFreeBytes)
                throw new InvalidOperationException("Stage pending copy, spool and reservation debt exceeds fresh physical headroom.");
        }
        // The SQL read is an await boundary. Recheck configuration before the
        // caller can confirm the plan or write physical bytes.
        if (!options.CurrentValue.UploadCapacityChecksEnabled
            || options.CurrentValue.UploadMinimumFreeBytes != minimumFreeBytes
            || !TryResolvePendingPath(row, out var finalPath, out var finalRoot)
            || !string.Equals(finalPath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !string.Equals(finalRoot, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Stage configuration changed during reservation budget verification.");
    }
}
