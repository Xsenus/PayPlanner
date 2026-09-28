using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

// Describes recorded copy integrity for the configured destinations, not restore readiness.
public static class DatabaseBackupSetStateEvaluator
{
    public static string Evaluate(IEnumerable<DatabaseBackupCopy> copies, bool s3Enabled,
        IEnumerable<string> s3ConnectionIds, bool yandexEnabled)
    {
        var inventory = copies.ToDictionary(copy => (copy.ProviderKey, copy.ConnectionId));
        if (!inventory.TryGetValue(("local", string.Empty), out var local)
            || local.Status == DatabaseBackupCopyStatuses.Deleted)
            return DatabaseBackupSetStatuses.NoLocalCopy;
        if (local.Status != DatabaseBackupCopyStatuses.Verified)
            return DatabaseBackupSetStatuses.Degraded;
        var destinations = new HashSet<(string Provider, string Connection)>();
        if (s3Enabled)
        {
            foreach (var id in s3ConnectionIds.Where(id => !string.IsNullOrWhiteSpace(id))) destinations.Add(("s3", id));
            if (destinations.Count == 0) return DatabaseBackupSetStatuses.Degraded;
        }
        if (yandexEnabled) destinations.Add(("yandexDisk", string.Empty));
        if (destinations.Count == 0) return DatabaseBackupSetStatuses.LocalVerified;

        var missing = false;
        var pending = false;
        foreach (var destination in destinations)
        {
            if (!inventory.TryGetValue(destination, out var copy)) { missing = true; continue; }
            if (copy.Status == DatabaseBackupCopyStatuses.Pending) pending = true;
            else if (copy.Status != DatabaseBackupCopyStatuses.Verified) return DatabaseBackupSetStatuses.Degraded;
        }
        if (pending) return DatabaseBackupSetStatuses.Replicating;
        return missing ? DatabaseBackupSetStatuses.AwaitingCopies : DatabaseBackupSetStatuses.VerifiedConfigured;
    }
}
