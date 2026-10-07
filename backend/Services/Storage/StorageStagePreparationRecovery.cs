using System.Data;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed partial class StorageStageCapacityService
{
    private async Task<string> ReconcilePreparationAsync(StorageCapacityReservation snapshot,
        StorageStageCapacityTicket ticket, bool apply, CancellationToken ct)
    {
        // Old rows default to false. Only a durable preparation that could not
        // issue a writer can bypass the persisted-marker requirement.
        if (!IsStageKey(snapshot.IdempotencyKey) || snapshot.ObjectId == Guid.Empty || snapshot.Bytes <= 0
            || !snapshot.IdempotencyKey.StartsWith(FencedStagePrefix, StringComparison.Ordinal)
            || snapshot.ProductContextJson is not null || snapshot.ReleasedAt is not null)
            return "invalidAuthority";
        // A present marker still requires the normal proof and exclusivity.
        // This also protects a live writer when a restored DB has older state.
        StorageStageWriterFence? marker;
        try
        {
            if (!TryResolvePendingPath(snapshot, out _, out var root)) return "invalidAuthority";
            try { marker = await StorageStageWriterFence.OpenForRecoveryAsync(root, ticket, ct); }
            catch (FileNotFoundException) { marker = null; }
            catch (DirectoryNotFoundException) { marker = null; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "writerNotProvenStopped";
        }
        await using var markerGuard = marker;
        var exact = db.StorageCapacityReservations.Where(row => row.IdempotencyKey == snapshot.IdempotencyKey
            && row.ResourceKey == snapshot.ResourceKey && row.ObjectId == snapshot.ObjectId
            && row.GroupId == snapshot.GroupId && row.BindingHash == snapshot.BindingHash
            && row.Bytes == snapshot.Bytes && row.LocalLocator == snapshot.LocalLocator
            && row.StageWriterMarkerIdentity == snapshot.StageWriterMarkerIdentity && row.StageRootIdentity == snapshot.StageRootIdentity
            && row.CreatedAt == snapshot.CreatedAt && row.UpdatedAt == snapshot.UpdatedAt
            && row.Status == StorageCapacityReservationStatuses.Pending && row.StageWriterAdmissionPending
            && row.ProductContextJson == null && row.ReleasedAt == null);
        bool MissingWithFreshAuthority()
        {
            if (!TryResolvePendingPath(snapshot, out var path, out var root)
                || !string.Equals(path, ticket.AbsolutePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            var physical = LocalStorageCapacityProbe.Observe(root, snapshot.ResourceKey, 0);
            if (physical.GroupId != snapshot.GroupId || physical.AvailableBytes is null) return false;
            try { _ = File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
        }
        if (!apply)
            return await exact.AsNoTracking().AnyAsync(ct) && MissingWithFreshAuthority()
                ? "eligibleMissingStage" : "writerNotProvenStopped";
        var releasedAt = DateTime.UtcNow;
        var commitStarted = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Competes with activation for the same row lock. Check the file
            // AFTER winning it; the producer cannot subsequently activate.
            if (await exact.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, StorageCapacityReservationStatuses.Released)
                .SetProperty(row => row.ReleasedAt, releasedAt).SetProperty(row => row.UpdatedAt, releasedAt), ct) != 1)
                return "writerNotProvenStopped";
            if (!MissingWithFreshAuthority()) return "writerNotProvenStopped"; // disposal rolls back
            await StorageCapacityAdmission.AdvanceEpochAsync(db, ct);
            commitStarted = true;
            await transaction.CommitAsync(ct);
            return "releasedMissingStage";
        }
        catch (Exception) when (commitStarted && !ct.IsCancellationRequested)
        {
            try
            {
                if (await db.StorageCapacityReservations.AsNoTracking().AnyAsync(row =>
                    row.IdempotencyKey == snapshot.IdempotencyKey && row.ResourceKey == snapshot.ResourceKey
                    && row.ObjectId == snapshot.ObjectId && row.GroupId == snapshot.GroupId
                    && row.BindingHash == snapshot.BindingHash && row.Bytes == snapshot.Bytes
                    && row.LocalLocator == snapshot.LocalLocator && row.CreatedAt == snapshot.CreatedAt
                    && row.StageWriterMarkerIdentity == snapshot.StageWriterMarkerIdentity && row.StageRootIdentity == snapshot.StageRootIdentity
                    && row.Status == StorageCapacityReservationStatuses.Released && row.StageWriterAdmissionPending
                    && row.ProductContextJson == null && row.ReleasedAt == releasedAt && row.UpdatedAt == releasedAt, ct))
                    return "releasedMissingStage";
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
            throw;
        }
        catch (Exception exception) when (StorageCapacityAdmission.IsConcurrentBudgetWrite(exception))
        {
            return "writerNotProvenStopped";
        }
    }
}
