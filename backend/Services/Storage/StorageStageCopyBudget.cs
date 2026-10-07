namespace PayPlanner.Api.Services.Storage;

public sealed partial class StorageStageCapacityService
{
    private async Task<(decimal Bytes, long AvailableBytes)> ReadCheckpointCopyBudgetAsync(
        StorageStageCapacityTicket ticket, long minimumFreeBytes, long availableBytes, CancellationToken ct)
    {
        var groups = await StorageCapacityAdmission.ReadPendingCopyGroupsAsync(db, ct).ConfigureAwait(false);
        if (groups.Length > StorageCapacityAdmission.MaximumResources)
            throw new InvalidOperationException("Stage copy capacity resource graph exceeds supported limits.");
        if (groups.Length == 0) return (0, availableBytes);

        // Every unsettled copy also needs a future source spool. Neither its
        // current lease nor a partly-written file proves that debt is settled.
        var spool = LocalStorageCapacityProbe.Observe(Path.GetTempPath(),
            StorageCapacityAdmission.SpoolResourceKey, minimumFreeBytes);
        RequireNativeObservation(spool);
        decimal bytes = 0;
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(group.ProviderKey) || group.Invalid != 0
                || group.Bytes is null or < 0 or > long.MaxValue)
                throw new InvalidOperationException("Stage copy debt has invalid authority or byte total.");
            if (spool.GroupId == ticket.GroupId)
            {
                bytes += group.Bytes.Value;
                availableBytes = Math.Min(availableBytes, spool.AvailableBytes!.Value);
            }
            if (!providers.TryGet(group.ProviderKey, out var provider))
                throw new InvalidOperationException("Stage copy destination provider is unknown.");
            if (provider.CapacityDomain == StorageCapacityDomain.Native && provider is IStorageCapacityProvider native)
            {
                // Only native adapters are queried here. Cloud quota remains
                // a scheduling prerequisite, not network I/O on every block.
                var destination = await native.ObserveCapacityAsync(
                    StorageCapacityAdmission.ResourceKey(group.ProviderKey, group.ConnectionId),
                    group.ConnectionId, minimumFreeBytes, ct).ConfigureAwait(false);
                RequireNativeObservation(destination);
                if (destination.GroupId == ticket.GroupId)
                {
                    bytes += group.Bytes.Value;
                    availableBytes = Math.Min(availableBytes, destination.AvailableBytes!.Value);
                }
            }
            else if (provider.CapacityDomain != StorageCapacityDomain.Remote)
                throw new InvalidOperationException("Stage copy destination has no authoritative capacity domain.");
            if (bytes > long.MaxValue)
                throw new InvalidOperationException("Stage copy debt exceeds supported byte totals.");
        }
        return (bytes, availableBytes);

        void RequireNativeObservation(StorageCapacityResource resource)
        {
            if (string.IsNullOrWhiteSpace(resource.GroupId) || resource.AvailableBytes is null or < 0
                || resource.MinimumFreeBytes != minimumFreeBytes)
                throw new InvalidOperationException("Stage copy native capacity is unknown.");
        }
    }
}
