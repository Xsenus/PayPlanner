using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using System.Text.Json;

namespace PayPlanner.Api.Services.Storage;

// Shared admission input for all storage queue writers. Provider observations
// happen before the transaction; authoritative debt is read inside it.
internal static class StorageCapacityAdmission
{
    private const string SpoolKey = "migration-source-spool";
    internal const int MaximumResources = 40;
    public static string SpoolResourceKey => SpoolKey;

    internal sealed record Observation(IReadOnlyDictionary<string, StorageCapacityResource> Resources, string? Error, long Epoch);
    internal sealed record Debt(IReadOnlyList<StorageCapacityDemand> Demands, string? Error);
    internal sealed record PendingCopyGroup(string? ProviderKey, string? ConnectionId, long Invalid, decimal? Bytes);

    public static string ResourceKey(string provider, string? connection)
        => JsonSerializer.Serialize(new[] { provider.ToLowerInvariant(), connection });

    private static IQueryable<StorageOperation> PendingCopies(PaymentContext db)
        => db.StorageOperations.AsNoTracking().Where(o =>
            (o.OperationType == StorageOperationTypes.Replicate || o.OperationType == StorageOperationTypes.Repair)
            && o.Status != StorageOperationStatuses.Succeeded && o.Status != StorageOperationStatuses.DeadLetter);

    private static IQueryable<StorageCapacityReservation> PendingReservations(PaymentContext db)
        => db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.Status == StorageCapacityReservationStatuses.Pending);

    public static async Task<Observation> ObserveAsync(PaymentContext db, IStorageProviderRegistry providers,
        IEnumerable<(string Provider, string? Connection)> writers, long minimumFreeBytes, CancellationToken ct)
    {
        // Capture the budget version before provider I/O. A release or another
        // scheduling decision during observation invalidates this snapshot.
        var epoch = await db.StorageCapacityEpochs.AsNoTracking().Where(e => e.Id == 1)
            .Select(e => (long?)e.Version).SingleOrDefaultAsync(ct);
        var snapshots = new Dictionary<string, StorageCapacityResource>(StringComparer.Ordinal)
        {
            [SpoolKey] = LocalStorageCapacityProbe.Observe(Path.GetTempPath(), SpoolKey, minimumFreeBytes)
        };
        if (epoch is null)
            return new(snapshots, "Storage capacity budget version is missing; reconcile the database before scheduling.", -1);
        var queuedResources = await PendingCopies(db)
            .Select(o => new { ProviderKey = o.ProviderKey == null ? null : o.ProviderKey.ToLower(), o.ConnectionId })
            .Distinct().OrderBy(o => o.ProviderKey).ThenBy(o => o.ConnectionId)
            .Take(MaximumResources + 1).ToArrayAsync(ct);
        if (queuedResources.Length > MaximumResources)
            return new(snapshots, "Capacity resource graph exceeds supported limits; reconcile the queue first.", epoch.Value);
        var reservedResources = await PendingReservations(db).Select(r => r.ResourceKey).Distinct()
            .OrderBy(key => key).Take(MaximumResources + 1).ToArrayAsync(ct);
        if (reservedResources.Length > MaximumResources)
            return new(snapshots, "Capacity reservation resources exceed supported limits; reconcile pending uploads first.", epoch.Value);
        var reservedWriters = new List<(string? Provider, string? Connection)>();
        foreach (var resource in reservedResources)
        {
            if (resource == SpoolKey) continue;
            string?[]? pair;
            try { pair = JsonSerializer.Deserialize<string?[]>(resource); }
            catch (JsonException) { return new(snapshots, "A pending capacity reservation has an invalid resource key.", epoch.Value); }
            if (pair is not { Length: 2 } || string.IsNullOrWhiteSpace(pair[0])
                || ResourceKey(pair[0]!, pair[1]) != resource)
                return new(snapshots, "A pending capacity reservation has an invalid resource key.", epoch.Value);
            reservedWriters.Add((pair[0], pair[1]));
        }
        var required = queuedResources.Select(o => (o.ProviderKey, o.ConnectionId))
            .Concat(reservedWriters)
            .Concat(writers.Select(o => ((string?)o.Provider, o.Connection)))
            .Distinct().ToArray();
        foreach (var (provider, connection) in required)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(provider))
                return new(snapshots, "An unsettled copy has no authoritative capacity resource.", epoch.Value);
            var key = ResourceKey(provider, connection);
            if (snapshots.ContainsKey(key)) continue;
            if (snapshots.Count > MaximumResources)
                return new(snapshots, "Capacity resource graph exceeds supported limits; reconcile the queue first.", epoch.Value);
            snapshots[key] = providers.TryGet(provider, out var registered) && registered is IStorageCapacityProvider observer
                ? await observer.ObserveCapacityAsync(key, connection, minimumFreeBytes, ct)
                : new(key, "", null, minimumFreeBytes, DateTimeOffset.UtcNow);
        }
        return new(snapshots, null, epoch.Value);
    }

    // Call only from the same serializable transaction that persists new copy
    // operations. SQL aggregation limits application memory to resource groups.
    public static async Task<Debt> ReadDebtAsync(PaymentContext db, Observation observation, CancellationToken ct)
    {
        var currentEpoch = await db.StorageCapacityEpochs.AsNoTracking().Where(e => e.Id == 1)
            .Select(e => (long?)e.Version).SingleOrDefaultAsync(ct);
        if (currentEpoch != observation.Epoch)
            return new([], "Storage capacity budget changed after observation; repeat with a fresh snapshot.");
        var resources = observation.Resources;
        var groups = await ReadPendingCopyGroupsAsync(db, ct);
        if (groups.Length > MaximumResources)
            return new([], "Capacity resource graph exceeds supported limits; reconcile the queue first.");
        var demands = new List<StorageCapacityDemand>(groups.Length * 2);
        foreach (var group in groups)
        {
            if (string.IsNullOrWhiteSpace(group.ProviderKey) || group.Invalid != 0 || group.Bytes is null or < 0 or > long.MaxValue)
                return new([], "Unsettled capacity debt has invalid authority, size or an unsupported byte total.");
            var resource = ResourceKey(group.ProviderKey, group.ConnectionId);
            if (!resources.ContainsKey(resource))
                return new([], "Capacity destinations changed during planning; prepare a new preview.");
            var bytes = (long)group.Bytes.Value;
            demands.Add(new(resource, bytes, "copy-debt:" + resource));
            demands.Add(new(SpoolKey, bytes, "spool-debt:" + resource));
        }
        var reserved = await PendingReservations(db).TagWith("storage-capacity-pending-reservations")
            .GroupBy(r => new { r.ResourceKey, r.GroupId })
            .Select(group => new
            {
                group.Key.ResourceKey,
                group.Key.GroupId,
                Invalid = group.LongCount(r => r.Bytes < 0 || r.IdempotencyKey == ""
                    || r.BindingHash == "" || r.LocalLocator == ""),
                Bytes = group.Sum(r => (decimal?)r.Bytes)
            })
            .OrderBy(group => group.ResourceKey).ThenBy(group => group.GroupId)
            .Take(MaximumResources + 1).ToArrayAsync(ct);
        if (reserved.Length > MaximumResources)
            return new([], "Capacity reservation resources exceed supported limits; reconcile pending uploads first.");
        foreach (var group in reserved)
        {
            if (string.IsNullOrWhiteSpace(group.ResourceKey) || string.IsNullOrWhiteSpace(group.GroupId)
                || group.Invalid != 0 || group.Bytes is null or < 0 or > long.MaxValue
                || !resources.TryGetValue(group.ResourceKey, out var observed) || observed.GroupId != group.GroupId)
                return new([], "Pending capacity reservation has invalid or changed resource authority.");
            demands.Add(new(group.ResourceKey, (long)group.Bytes.Value,
                "reservation-debt:" + group.ResourceKey + ":" + group.GroupId));
        }
        return new(demands, null);
    }

    // One shared SQL projection for scheduling and checkpoint verification.
    // The queue stays in SQL; only at most 41 destination groups are returned.
    internal static Task<PendingCopyGroup[]> ReadPendingCopyGroupsAsync(PaymentContext db, CancellationToken ct)
        => PendingCopies(db).TagWith("storage-capacity-pending-budget")
            .GroupBy(o => new { ProviderKey = o.ProviderKey == null ? null : o.ProviderKey.ToLower(), o.ConnectionId })
            .OrderBy(g => g.Key.ProviderKey).ThenBy(g => g.Key.ConnectionId)
            .Select(g => new PendingCopyGroup(
                g.Key.ProviderKey,
                g.Key.ConnectionId,
                g.LongCount(o => o.StorageObjectGeneration == null || o.StorageObjectGeneration.SizeBytes < 0
                    || o.IdempotencyKey == "" || o.IdempotencyKey.Trim() == ""),
                g.Sum(o => (decimal?)o.StorageObjectGeneration!.SizeBytes)))
            .Take(MaximumResources + 1).ToArrayAsync(ct);

    // Must be executed inside the same serializable transaction as new durable
    // demand or a reservation release. Losing the compare-and-swap aborts it.
    public static async Task<bool> TryAdvanceEpochAsync(PaymentContext db, Observation observed, CancellationToken ct)
        => observed.Epoch >= 0 && observed.Epoch < long.MaxValue
            && await db.StorageCapacityEpochs.Where(e => e.Id == 1 && e.Version == observed.Epoch)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, e => e.Version + 1), ct) == 1;

    // A worker settling old copy debt has no capacity observation to compare,
    // but must invalidate observations made before the physical write settled.
    public static async Task AdvanceEpochAsync(PaymentContext db, CancellationToken ct)
    {
        if (await db.StorageCapacityEpochs.Where(e => e.Id == 1 && e.Version < long.MaxValue)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Version, e => e.Version + 1), ct) != 1)
            throw new InvalidOperationException("Storage capacity budget version is missing or exhausted.");
    }

    public static bool IsConcurrentBudgetWrite(Exception exception)
    {
        if (FinancialConcurrencyGuard.IsSerializationFailure(exception)) return true;
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
        return false;
    }

    public static StorageCapacityBudgetReport Evaluate(Observation observed, Debt debt,
        IReadOnlyList<StorageCapacityDemand> proposed)
        => StorageCapacityBudget.Evaluate(observed.Resources.Values.ToArray(), debt.Demands, proposed,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

    public static StorageCapacityDemand CopyDemand(string provider, string? connection, long size, string idempotencyKey)
        => new(ResourceKey(provider, connection), size, "copy:" + idempotencyKey);

    public static StorageCapacityDemand SpoolDemand(long size, string idempotencyKey)
        => new(SpoolKey, size, "spool:" + idempotencyKey);
}
