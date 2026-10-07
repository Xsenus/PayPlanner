namespace PayPlanner.Api.Services.Storage;

// Resource keys describe destinations/spool areas; group IDs describe shared
// physical capacity. Adapters, not folder names, must supply verified group IDs.
public sealed record StorageCapacityResource(string Key, string GroupId, long? AvailableBytes,
    long MinimumFreeBytes, DateTimeOffset ObservedAt);

public sealed record StorageCapacityDemand(string ResourceKey, long Bytes, string OperationIdentity);

public sealed record StorageCapacityGroupBudget(string GroupId, long? AvailableBytes, long MinimumFreeBytes,
    long PendingBytes, long AdditionalBytes, long? RemainingBytes, bool CanSchedule);

public sealed record StorageCapacityBudgetReport(bool CanSchedule,
    IReadOnlyList<StorageCapacityGroupBudget> Groups, IReadOnlyList<string> Errors);

public static class StorageCapacityBudget
{
    // Pending includes durable unsettled copy/spool reservations. New work that
    // already has the exact same reservation is counted once, not on each retry.
    // This calculation is NOT an atomic reservation or an observation of a disk.
    public static StorageCapacityBudgetReport Evaluate(IReadOnlyList<StorageCapacityResource> resources,
        IReadOnlyList<StorageCapacityDemand> pending, IReadOnlyList<StorageCapacityDemand> proposed,
        DateTimeOffset now, TimeSpan maximumAge)
    {
        const int maximumItems = 10000;
        var errors = new List<string>();
        if (resources.Count > maximumItems || pending.Count > maximumItems || proposed.Count > maximumItems
            || maximumAge <= TimeSpan.Zero)
            return new(false, [], ["Capacity inputs exceed supported limits or have an invalid observation lifetime."]);
        var byKey = new Dictionary<string, StorageCapacityResource>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            if (string.IsNullOrWhiteSpace(resource.Key) || string.IsNullOrWhiteSpace(resource.GroupId)
                || resource.MinimumFreeBytes < 0 || resource.AvailableBytes is < 0
                || resource.ObservedAt > now || now - resource.ObservedAt > maximumAge
                || !byKey.TryAdd(resource.Key, resource))
                errors.Add("A capacity resource is invalid, duplicated or not freshly observed.");
        }
        var identities = new Dictionary<string, StorageCapacityDemand>(StringComparer.Ordinal);
        var pendingByGroup = new Dictionary<string, long>(StringComparer.Ordinal);
        var newByGroup = new Dictionary<string, long>(StringComparer.Ordinal);
        void Accumulate(IReadOnlyList<StorageCapacityDemand> demands, Dictionary<string, long> totals)
        {
            foreach (var demand in demands)
            {
                if (demand.Bytes < 0 || string.IsNullOrWhiteSpace(demand.OperationIdentity)
                    || string.IsNullOrWhiteSpace(demand.ResourceKey) || !byKey.TryGetValue(demand.ResourceKey, out var resource))
                { errors.Add("A capacity demand has no valid size, identity or observed resource."); continue; }
                if (identities.TryGetValue(demand.OperationIdentity, out var existing))
                {
                    if (existing != demand) errors.Add("A reservation identity has conflicting capacity authority.");
                    continue;
                }
                identities.Add(demand.OperationIdentity, demand);
                try { totals[resource.GroupId] = checked(totals.GetValueOrDefault(resource.GroupId) + demand.Bytes); }
                catch (OverflowException) { errors.Add("Capacity demand exceeds supported byte counts."); }
            }
        }
        Accumulate(pending, pendingByGroup);
        Accumulate(proposed, newByGroup);
        var groups = new List<StorageCapacityGroupBudget>();
        foreach (var group in byKey.Values.GroupBy(r => r.GroupId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            // Different observations of one shared volume cannot be added up.
            // An unknown member also prevents certifying this shared budget.
            var available = group.All(r => r.AvailableBytes.HasValue) ? group.Min(r => r.AvailableBytes) : null;
            var reserve = group.Max(r => r.MinimumFreeBytes);
            var debt = pendingByGroup.GetValueOrDefault(group.Key);
            var additional = newByGroup.GetValueOrDefault(group.Key);
            long? remaining = null;
            try { if (available.HasValue) remaining = checked(available.Value - checked(debt + additional)); }
            catch (OverflowException) { errors.Add("Combined capacity demand exceeds supported byte counts."); }
            groups.Add(new(group.Key, available, reserve, debt, additional, remaining, remaining >= reserve));
        }
        return new(errors.Count == 0 && groups.All(g => g.CanSchedule), groups, errors);
    }
}
