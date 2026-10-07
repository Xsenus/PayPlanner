using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// A pending reservation survives an interrupted write. It is released only in
// the transaction that publishes the object and its unsettled copy operations.
internal static class StorageUploadCapacityReservations
{
    internal sealed record Ticket(string Key, string BindingHash, Guid ObjectId, string LocalLocator,
        long SizeBytes, string Sha256, IReadOnlyDictionary<string, long> Amounts,
        IReadOnlyDictionary<string, string> Groups, IReadOnlyDictionary<string, string?> Connections);

    internal static string BindingHash(StorageObjectWriteRequest request, StoragePolicyEvaluation policy,
        StorageOrchestrationOptions settings)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            request.DataClass,
            request.OwnerType,
            request.OwnerId,
            request.LogicalName,
            request.ContentType,
            request.CreatedByUserId,
            request.ProductContextJson,
            Size = request.ExpectedSizeBytes!.Value,
            Sha = request.ExpectedSha256!.ToLowerInvariant(),
            policy.PolicyVersion,
            MinimumFreeBytes = settings.UploadMinimumFreeBytes,
            Destinations = policy.WriteProviders.Where(p => !p.Equals("local", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Provider: p.ToLowerInvariant(), Connection: settings.ProviderConnections.GetValueOrDefault(p)?.Trim())).ToArray()
        })))).ToLowerInvariant();

    public static async Task<(Ticket? Ticket, string? Error)> PrepareAsync(PaymentContext db,
        IStorageProviderRegistry providers, IOptionsMonitor<StorageOrchestrationOptions> options,
        StoragePolicyEvaluation policy, StorageObjectWriteRequest request, string key,
        Guid proposedObjectId, string proposedLocator, CancellationToken ct)
    {
        if (request.ExpectedSizeBytes is not { } size || size < 0
            || request.ExpectedSha256 is not { Length: 64 } sha
            || !sha.All(Uri.IsHexDigit))
            return (null, "Capacity-checked uploads require an exact size and SHA-256 checksum.");
        if (db.ChangeTracker.HasChanges())
            return (null, "Upload reservation requires a clean database unit of work.");
        var settings = options.CurrentValue;
        if (!settings.UploadCapacityChecksEnabled)
            return (null, "Upload capacity policy changed during reservation; retry the upload.");
        var minimumFreeBytes = settings.UploadMinimumFreeBytes;
        if (minimumFreeBytes < 0)
            return (null, "Upload minimum free space must be non-negative.");
        var destinations = policy.WriteProviders.Where(p => !p.Equals("local", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Provider: p.ToLowerInvariant(), Connection: settings.ProviderConnections.GetValueOrDefault(p)?.Trim()))
            .ToArray();
        var connections = destinations.ToDictionary(d => d.Provider, d => d.Connection, StringComparer.OrdinalIgnoreCase);
        var binding = BindingHash(request, policy, settings);
        var amounts = new Dictionary<string, long>(StringComparer.Ordinal);
        void Add(string resource, long bytes)
            => amounts[resource] = checked(amounts.GetValueOrDefault(resource) + bytes);
        try
        {
            Add(StorageCapacityAdmission.ResourceKey("local", null), size);
            foreach (var destination in destinations)
            {
                if (string.IsNullOrWhiteSpace(destination.Connection))
                    return (null, "An upload destination has no explicit storage connection.");
                Add(StorageCapacityAdmission.ResourceKey(destination.Provider, destination.Connection), size);
                Add(StorageCapacityAdmission.SpoolResourceKey, size);
            }
        }
        catch (OverflowException)
        {
            return (null, "Upload capacity demand exceeds supported byte counts.");
        }
        if (amounts.Count > 40)
            return (null, "Upload capacity resource graph exceeds supported limits.");

        var observed = await StorageCapacityAdmission.ObserveAsync(db, providers,
            new[] { (Provider: "local", Connection: (string?)null) }.Concat(destinations),
            minimumFreeBytes, ct);
        if (observed.Error is not null) return (null, observed.Error);
        var checkedAt = DateTimeOffset.UtcNow;
        if (amounts.Keys.Any(key => !observed.Resources.TryGetValue(key, out var resource)
            || string.IsNullOrWhiteSpace(resource.GroupId) || resource.AvailableBytes is null or < 0
            || resource.ObservedAt > checkedAt || checkedAt - resource.ObservedAt > TimeSpan.FromMinutes(1)))
            return (null, "Upload capacity authority is unavailable.");
        var groups = amounts.Keys.ToDictionary(key => key, key => observed.Resources[key].GroupId, StringComparer.Ordinal);
        var existing = await db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.IdempotencyKey == key).Take(41).ToArrayAsync(ct);
        if (existing.Length > 0)
        {
            if (existing.Length != amounts.Count || existing.Any(r => r.Status != StorageCapacityReservationStatuses.Pending
                || r.BindingHash != binding || r.Bytes != amounts.GetValueOrDefault(r.ResourceKey, -1)
                || !observed.Resources.TryGetValue(r.ResourceKey, out var resource) || resource.GroupId != r.GroupId
                || r.ObjectId != existing[0].ObjectId || r.LocalLocator != existing[0].LocalLocator
                || r.ProductContextJson != (r.ResourceKey == StorageCapacityAdmission.ResourceKey("local", null) ? request.ProductContextJson : null)))
                return (null, "An existing upload reservation has different content or capacity authority.");
            return (new(key, binding, existing[0].ObjectId, existing[0].LocalLocator, size,
                sha.ToLowerInvariant(), amounts, groups, connections), null);
        }
        var rows = new List<StorageCapacityReservation>(amounts.Count);
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var debt = await StorageCapacityAdmission.ReadDebtAsync(db, observed, ct);
            if (debt.Error is not null) return (null, debt.Error);
            var demands = amounts.Select(pair => new StorageCapacityDemand(pair.Key, pair.Value,
                "upload:" + key + ":" + pair.Key)).ToArray();
            var report = StorageCapacityAdmission.Evaluate(observed, debt, demands);
            if (!report.CanSchedule)
                return (null, "Storage capacity is unknown, stale or insufficient; upload was not reserved.");
            if (!options.CurrentValue.UploadCapacityChecksEnabled
                || options.CurrentValue.UploadMinimumFreeBytes != minimumFreeBytes)
                return (null, "Upload capacity policy changed during reservation; retry the upload.");
            var now = DateTime.UtcNow;
            foreach (var amount in amounts)
            {
                rows.Add(new StorageCapacityReservation
                {
                    IdempotencyKey = key,
                    ResourceKey = amount.Key,
                    GroupId = observed.Resources[amount.Key].GroupId,
                    BindingHash = binding,
                    Bytes = amount.Value,
                    ObjectId = proposedObjectId,
                    LocalLocator = proposedLocator,
                    ProductContextJson = amount.Key == StorageCapacityAdmission.ResourceKey("local", null) ? request.ProductContextJson : null,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            db.StorageCapacityReservations.AddRange(rows);
            if (!await StorageCapacityAdmission.TryAdvanceEpochAsync(db, observed, ct))
                return (null, "Storage capacity budget changed during reservation; retry the upload.");
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (new(key, binding, proposedObjectId, proposedLocator, size, sha.ToLowerInvariant(),
                amounts, groups, connections), null);
        }
        catch (Exception ex) when (StorageCapacityAdmission.IsConcurrentBudgetWrite(ex)
            || ex is DbUpdateException { InnerException: Npgsql.PostgresException { SqlState: "23505" } }
            || ex is DbUpdateException { InnerException: Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 } })
        {
            return (null, "Concurrent upload changed capacity; retry the upload with the same key.");
        }
        finally
        {
            foreach (var row in rows) db.Entry(row).State = EntityState.Detached;
        }
    }

    internal static Task<string?> ReadProductContextAsync(PaymentContext db, string reservationKey, CancellationToken ct)
    {
        var localResource = StorageCapacityAdmission.ResourceKey("local", null);
        return db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.IdempotencyKey == reservationKey && r.ResourceKey == localResource && r.Status == StorageCapacityReservationStatuses.Pending)
            .Select(r => r.ProductContextJson).SingleOrDefaultAsync(ct);
    }

    public static async Task ReleaseForPublicationAsync(PaymentContext db, Ticket ticket, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Upload reservation release requires the publication transaction.");
        var rows = await db.StorageCapacityReservations.Where(r => r.IdempotencyKey == ticket.Key)
            .Take(41).ToArrayAsync(ct);
        if (rows.Length != ticket.Amounts.Count || rows.Any(r => r.Status != StorageCapacityReservationStatuses.Pending
            || r.BindingHash != ticket.BindingHash || r.ObjectId != ticket.ObjectId
            || r.LocalLocator != ticket.LocalLocator
            || !ticket.Amounts.TryGetValue(r.ResourceKey, out var bytes) || r.Bytes != bytes
            || !ticket.Groups.TryGetValue(r.ResourceKey, out var group) || r.GroupId != group))
            throw new InvalidOperationException("Upload reservation changed before publication.");
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.Status = StorageCapacityReservationStatuses.Released;
            row.ReleasedAt = now;
            row.UpdatedAt = now;
        }
        await StorageCapacityAdmission.AdvanceEpochAsync(db, ct);
    }
}
