using System.IO.Compression;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupManagedBinding(
    DatabaseBackupStorageReplicaReference SelectedReplica, string ArchivePartRelativePath,
    string ArchiveEntryPath, long ActualSizeBytes, string ActualSha256);

/// <summary>
/// Streams every catalog generation, including deleted/held generations. A candidate
/// is sealed into the archive only after its bytes match the immutable snapshot facts.
/// Owns one disposable disk spool, not a complete list of generations or file bytes.
/// The caller owns inventory/archive/mapping publication and manifest checks.
/// </summary>
public static class DatabaseBackupManagedArchive
{
    public static async Task<long> WriteAsync(Stream inventory, ZipArchive archive,
        string archivePartRelativePath, string stagingDirectory,
        Func<DatabaseBackupStorageReplicaReference, CancellationToken, Task<StorageReadResult>> openReplica,
        Func<DatabaseBackupManagedBinding, CancellationToken, Task> writeBinding,
        CancellationToken cancellationToken)
    {
        ValidatePath(archivePartRelativePath);
        var temporaryPath = Path.Combine(stagingDirectory, $"managed-backup-{Guid.NewGuid():N}.tmp");
        await using var spool = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        DatabaseBackupStorageReplicaReference? generation = null;
        DatabaseBackupStorageReplicaReference? selected = null;
        DatabaseBackupArchiveFileHash? selectedHash = null;
        long? previousReplicaId = null;
        long count = 0;

        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken))
        {
            if (row.Replica is not { } replica) continue;
            if (generation is null || generation.GenerationId != replica.GenerationId)
            {
                if (generation is not null)
                {
                    await SealGenerationAsync();
                    if (string.CompareOrdinal(generation.GenerationId.ToString("D"), replica.GenerationId.ToString("D")) >= 0)
                        throw new InvalidDataException("Поколения снимка не упорядочены или повторяются.");
                }
                generation = replica;
                selected = null;
                selectedHash = null;
                previousReplicaId = null;
            }
            else
            {
                if (GenerationFacts(generation) != GenerationFacts(replica)
                    || previousReplicaId is null || replica.ReplicaId is null || replica.ReplicaId <= previousReplicaId)
                    throw new InvalidDataException("Реплики не соответствуют одному поколению снимка.");
            }
            previousReplicaId = replica.ReplicaId;
            if (selected is not null || replica.ReplicaStatus != StorageReplicaStatuses.Available) continue;
            cancellationToken.ThrowIfCancellationRequested();
            spool.Position = 0;
            spool.SetLength(0);
            try
            {
                await using var read = await openReplica(replica, cancellationToken);
                if (read.Status != StorageOperationStatus.Succeeded || read.Content is null) continue;
                if (read.SizeBytes != replica.SizeBytes) continue;
                var hash = await DatabaseBackupArchiveHash.CopyAsync(read.Content, spool, cancellationToken, replica.SizeBytes);
                if (hash.SizeBytes != replica.SizeBytes
                    || !string.Equals(hash.Sha256, replica.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                selected = replica;
                selectedHash = hash;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
                or HttpRequestException or TimeoutException or OperationCanceledException)
            {
                // A missing/corrupt/unavailable replica never enters the archive.
                // The next candidate still has to match the snapshot, not live catalog facts.
            }
        }
        if (generation is not null) await SealGenerationAsync();
        return count;

        async Task SealGenerationAsync()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected is null || selectedHash is null)
                throw new InvalidDataException($"Не удалось сохранить байты поколения {generation!.GenerationId:D} из снимка базы.");
            var entryPath = $"managed-generations/{selected.GenerationId:D}.bin";
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            spool.Position = 0;
            await using (var destination = entry.Open())
            {
                var actual = await DatabaseBackupArchiveHash.CopyAsync(spool, destination, cancellationToken, selected.SizeBytes);
                if (actual != selectedHash)
                    throw new InvalidDataException("Проверенные байты поколения изменились до записи в архив.");
            }
            entry.Comment = DatabaseBackupArchiveHash.ToComment(selectedHash.Sha256);
            await writeBinding(new(selected, archivePartRelativePath, entryPath,
                selectedHash.SizeBytes, selectedHash.Sha256), cancellationToken);
            count = checked(count + 1);
        }
    }

    private static DatabaseBackupStorageReplicaReference GenerationFacts(DatabaseBackupStorageReplicaReference value)
        => value with { ReplicaId = null, ProviderKey = null, ConnectionId = null, Locator = null, ReplicaStatus = null };

    private static void ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\')
            || path.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.Contains('\0')))
            throw new InvalidDataException("Некорректный путь архивной части поколений.");
    }
}
