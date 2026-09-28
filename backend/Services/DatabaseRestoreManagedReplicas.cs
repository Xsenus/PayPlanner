using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services;

/// <summary>Rebinds only the disposable restored catalog after snapshot and byte verification.</summary>
internal static class DatabaseRestoreManagedReplicas
{
    public static async Task PrepareAsync(PaymentContext restored, FileStream mapping,
        DatabaseBackupPartManifest mappingPart, string inventorySha, string filesRoot, CancellationToken token)
    {
        await CheckMappingAsync();
        mapping.Position = 0;
        var root = Path.GetFullPath(filesRoot).TrimEnd(Path.DirectorySeparatorChar);
        await using var transaction = restored.Database.CurrentTransaction is null
            ? await restored.Database.BeginTransactionAsync(token) : null;
        var batch = new List<DatabaseBackupManagedBinding>(250);
        await foreach (var binding in DatabaseBackupManagedBindings.ReadAsync(mapping, inventorySha, token))
        {
            batch.Add(binding);
            if (batch.Count == 250) { await ApplyBatchAsync(); batch.Clear(); }
        }
        if (batch.Count > 0) await ApplyBatchAsync();
        // A local verification instance must not replay source replication/delete jobs.
        await restored.StorageOperations.Where(item => item.Status == StorageOperationStatuses.Pending
            || item.Status == StorageOperationStatuses.Running || item.Status == StorageOperationStatuses.Unknown)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, StorageOperationStatuses.DeadLetter)
                .SetProperty(item => item.LastError, "Изолированная копия: исходные операции хранилища отключены.")
                .SetProperty(item => item.LeaseOwner, (string?)null)
                .SetProperty(item => item.LeaseExpiresAt, (DateTime?)null), token);
        await CheckMappingAsync();
        if (transaction is not null) await transaction.CommitAsync(token);

        async Task CheckMappingAsync()
        {
            mapping.Position = 0;
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(mapping, token));
            if (mapping.Length != mappingPart.SizeBytes || !string.Equals(sha, mappingPart.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сопоставление восстановленных реплик изменилось.");
        }

        async Task ApplyBatchAsync()
        {
            var replicaIds = batch.Select(item => item.SelectedReplica.ReplicaId!.Value).ToArray();
            var generations = batch.Select(item => item.SelectedReplica.GenerationId).ToArray();
            var rows = await restored.StorageReplicas.Include(item => item.StorageObjectGeneration)
                .Where(item => replicaIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
            if (rows.Count != batch.Count) throw new InvalidDataException("В восстановленном каталоге отсутствует выбранная реплика.");
            var now = DateTime.UtcNow;
            foreach (var binding in batch)
            {
                var reference = binding.SelectedReplica;
                var replica = rows[reference.ReplicaId!.Value];
                var generation = replica.StorageObjectGeneration;
                if (generation.Id != reference.GenerationId || generation.StorageObjectId != reference.ObjectId
                    || generation.Generation != reference.Generation || generation.SizeBytes != binding.ActualSizeBytes
                    || !string.Equals(generation.Sha256, binding.ActualSha256, StringComparison.OrdinalIgnoreCase)
                    || replica.ProviderKey != reference.ProviderKey || replica.ConnectionId != reference.ConnectionId
                    || replica.Locator != reference.Locator || replica.Status != reference.ReplicaStatus)
                    throw new InvalidDataException("Выбранная восстановленная реплика не соответствует снимку.");
                DatabaseBackupNativeBindings.ValidatePortablePath(binding.ArchiveEntryPath);
                var path = Path.GetFullPath(Path.Combine(root, binding.ArchiveEntryPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("Восстановленный файл находится вне изолированного каталога.");
                for (var check = path; check is not null; check = Path.GetDirectoryName(check))
                {
                    if ((File.GetAttributes(check) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Ссылки внутри восстановленного хранилища запрещены.");
                    if (string.Equals(check, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
                }
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                var actual = await DatabaseBackupArchiveHash.CopyAsync(input, Stream.Null, token);
                if (actual.SizeBytes != binding.ActualSizeBytes
                    || !string.Equals(actual.Sha256, binding.ActualSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Байты восстановленной реплики не соответствуют сопоставлению.");
                replica.ProviderKey = "local";
                replica.ConnectionId = null;
                replica.Locator = binding.ArchiveEntryPath;
                replica.Status = StorageReplicaStatuses.Available;
                replica.SizeBytes = actual.SizeBytes;
                replica.Sha256 = actual.Sha256;
                replica.ProviderVersion = null;
                replica.LastError = null;
                replica.LastVerifiedAt = now;
                replica.UpdatedAt = now;
            }
            await restored.StorageReplicas.Where(item => generations.Contains(item.StorageObjectGenerationId)
                && !replicaIds.Contains(item.Id)).ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, StorageReplicaStatuses.Missing)
                    .SetProperty(item => item.LastVerifiedAt, (DateTime?)null)
                    .SetProperty(item => item.LastError, "Изолированная копия: прежний адрес реплики не используется."), token);
            await restored.SaveChangesAsync(token);
            restored.ChangeTracker.Clear();
        }
    }
}
