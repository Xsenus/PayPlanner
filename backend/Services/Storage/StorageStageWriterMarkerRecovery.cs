using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageStageWriterMarkerRecoveryReport(int Inspected, int Eligible, int Removed,
    bool HasMore, IReadOnlyList<StorageStageRecoveryItem> Items)
{
    public string? NextAfterKey { get; init; }
}

public interface IStorageStageWriterMarkerRecoveryService
{
    Task<StorageStageWriterMarkerRecoveryReport> ReconcileReleasedWriterMarkersAsync(
        bool apply, int limit, TimeSpan minimumAge, CancellationToken ct, string? afterKey = null);
}

public sealed partial class StorageStageCapacityService : IStorageStageWriterMarkerRecoveryService
{
    // This is metadata housekeeping, not pending debt reconciliation. In
    // particular it must never open, delete or settle the original stage file.
    public async Task<StorageStageWriterMarkerRecoveryReport> ReconcileReleasedWriterMarkersAsync(
        bool apply, int limit, TimeSpan minimumAge, CancellationToken ct, string? afterKey = null)
    {
        if (limit is < 1 or > 200 || minimumAge < TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(limit), "Marker recovery requires 1–200 rows and at least ten minutes of age.");
        if (afterKey is not null && (!IsStageKey(afterKey) || !afterKey.StartsWith(FencedStagePrefix, StringComparison.Ordinal)))
            throw new ArgumentException("Marker recovery cursor is invalid.", nameof(afterKey));
        if (db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Marker recovery requires a clean database unit of work.");
        var cutoff = DateTime.UtcNow - minimumAge;
        var query = db.StorageCapacityReservations.AsNoTracking().Where(row =>
            row.Status == StorageCapacityReservationStatuses.Released && row.ReleasedAt != null
            && row.ReleasedAt <= cutoff && row.IdempotencyKey.StartsWith(FencedStagePrefix));
        if (afterKey is not null) query = query.Where(row => string.Compare(row.IdempotencyKey, afterKey) > 0);
        var rows = await query.OrderBy(row => row.IdempotencyKey).Take(limit + 1).ToArrayAsync(ct);
        var items = new List<StorageStageRecoveryItem>(Math.Min(rows.Length, limit));
        var eligible = 0;
        var removed = 0;
        foreach (var row in rows.Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsStageKey(row.IdempotencyKey) || !TryResolvePendingPath(row, out var path, out var root))
            {
                items.Add(ToRecoveryItem(row, "invalidAuthority"));
                continue;
            }
            var physical = LocalStorageCapacityProbe.Observe(root, row.ResourceKey, 0);
            if (physical.GroupId != row.GroupId || physical.AvailableBytes is null)
            {
                items.Add(ToRecoveryItem(row, "changedVolume", pathValidated: true));
                continue;
            }
            var ticket = new StorageStageCapacityTicket(row.IdempotencyKey, row.BindingHash, row.ResourceKey,
                row.GroupId, row.LocalLocator, path, row.Bytes, row.ObjectId)
            { WriterMarkerIdentity = row.StageWriterMarkerIdentity, RootIdentity = row.StageRootIdentity };
            try
            {
                await using var fence = await StorageStageWriterFence.OpenForRecoveryAsync(root, ticket, ct);
                // Read after acquiring the SAME persisted marker. Snapshot
                // identity, receipt and release state must all still match.
                if (!await db.StorageCapacityReservations.AsNoTracking().AnyAsync(current =>
                    current.IdempotencyKey == row.IdempotencyKey && current.ResourceKey == row.ResourceKey
                    && current.GroupId == row.GroupId && current.BindingHash == row.BindingHash
                    && current.Bytes == row.Bytes && current.ObjectId == row.ObjectId
                    && current.LocalLocator == row.LocalLocator && current.ProductContextJson == row.ProductContextJson
                    && current.StageWriterAdmissionPending == row.StageWriterAdmissionPending
                    && current.StageWriterMarkerIdentity == row.StageWriterMarkerIdentity && current.StageRootIdentity == row.StageRootIdentity
                    && current.Status == StorageCapacityReservationStatuses.Released
                    && current.ReleasedAt == row.ReleasedAt && current.UpdatedAt == row.UpdatedAt
                    && current.CreatedAt == row.CreatedAt, ct))
                {
                    items.Add(ToRecoveryItem(row, "changedAuthority", pathValidated: true));
                    continue;
                }
                eligible++;
                ct.ThrowIfCancellationRequested();
                if (apply && await fence.RetireAndConfirmAbsenceAsync())
                {
                    removed++;
                    items.Add(ToRecoveryItem(row, "releasedMarkerRemoved", pathValidated: true));
                }
                else items.Add(ToRecoveryItem(row, apply ? "markerRetained" : "eligibleReleasedMarker", pathValidated: true));
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                items.Add(ToRecoveryItem(row, "markerAlreadyAbsent", pathValidated: true));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unknown authority or a live holder never authorizes removal.
                items.Add(ToRecoveryItem(row, "markerNotProvenStopped", pathValidated: true));
            }
        }
        return new(items.Count, eligible, removed, rows.Length > limit, items)
        {
            NextAfterKey = rows.Length > limit ? rows[limit - 1].IdempotencyKey : null
        };
    }
}
