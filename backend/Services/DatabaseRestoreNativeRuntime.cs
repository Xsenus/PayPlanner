using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services;

/// <summary>Prepares native paths only inside the caller's disposable database transaction.</summary>
internal static class DatabaseRestoreNativeRuntime
{
    public const long CapacityReserveBytes = 64L * 1024 * 1024;

    public static async Task<IReadOnlyDictionary<string, string?>> PrepareAsync(PaymentContext db,
        DatabaseBackupManifest manifest, FileStream mapping, DatabaseBackupPartManifest mappingPart,
        string inventorySha, string filesRoot, string runtimeRoot, CancellationToken token,
        Func<string, long>? availableFreeBytes = null)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Disposable preparation requires a transaction.");
        var roots = manifest.Parts!.Where(part => part.Kind == "file-storage")
            .SelectMany(part => part.FileRoots ?? []).ToArray();
        DatabaseBackupFileLayout.ValidateRootMappings(roots);
        var settings = new Dictionary<string, string?>
        {
            [ApplicationFileStoragePaths.RootPathConfigurationKey] = Root("file-storage"),
            [ElectronicSignatureStoragePaths.StorageRootConfigurationKey] = Path.Combine(runtimeRoot, "signatures"),
            [UserPhotoStoragePaths.StorageRootConfigurationKey] = Path.Combine(runtimeRoot, "photos"),
            [Storage.LocalStoragePaths.RootConfigurationKey] = filesRoot,
        };
        EnsureOutputDirectory(Path.GetDirectoryName(Path.GetFullPath(runtimeRoot))!);
        if (Directory.Exists(runtimeRoot) || File.Exists(runtimeRoot))
            throw new IOException("Каталог подготовки уже существует.");
        await CheckMappingAsync();
        mapping.Position = 0;
        var requiredBytes = await CalculateRequiredBytesAsync(mapping, inventorySha, token);
        await CheckMappingAsync();
        token.ThrowIfCancellationRequested();
        var freeBytes = (availableFreeBytes ?? GetAvailableFreeBytes)(Path.GetDirectoryName(Path.GetFullPath(runtimeRoot))!);
        token.ThrowIfCancellationRequested();
        if (freeBytes < requiredBytes)
            throw new IOException("Недостаточно доступного места для подготовки восстановленных подписей и фотографий.");
        mapping.Position = 0;
        Directory.CreateDirectory(runtimeRoot);
        EnsureOutputDirectory(runtimeRoot);
        var batch = new List<DatabaseBackupNativeBinding>(250);
        await foreach (var binding in DatabaseBackupNativeBindings.ReadAsync(mapping, inventorySha, true, token))
        {
            batch.Add(binding);
            if (batch.Count == 250) { await ApplyAsync(); batch.Clear(); }
        }
        if (batch.Count > 0) await ApplyAsync();
        await CheckMappingAsync();
        return settings;

        string Root(string name)
        {
            var prefix = roots.SingleOrDefault(item => item.Name == name)?.ArchivePrefix ?? name;
            DatabaseBackupNativeBindings.ValidatePortablePath(prefix);
            return Path.GetFullPath(Path.Combine(filesRoot, prefix.Replace('/', Path.DirectorySeparatorChar)));
        }

        async Task CheckMappingAsync()
        {
            mapping.Position = 0;
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(mapping, token));
            if (mapping.Length != mappingPart.SizeBytes || !string.Equals(sha, mappingPart.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Сопоставление обычных документов изменилось.");
        }

        async Task ApplyAsync()
        {
            var originals = Ids("esign-original");
            var artifacts = Ids("esign-artifact");
            var photos = Ids("profile-photo");
            var envelopes = originals.Length == 0 ? [] : await db.ElectronicSignatureEnvelopes.Where(row => originals.Contains(row.Id)).ToDictionaryAsync(row => row.Id, token);
            var evidence = artifacts.Length == 0 ? [] : await db.ElectronicSignatureArtifacts.Where(row => artifacts.Contains(row.Id)).ToDictionaryAsync(row => row.Id, token);
            var users = photos.Length == 0 ? [] : await db.Users.Where(row => photos.Contains(row.Id)).ToDictionaryAsync(row => row.Id, token);
            foreach (var binding in batch)
            {
                var reference = binding.Reference;
                Action<string>? update = null;
                string? folder = null;
                if (reference.SourceKind == "esign-original")
                {
                    if (!envelopes.TryGetValue(reference.SourceId, out var row)
                        || reference != new DatabaseBackupStoredFileReference("esign-original", row.Id, row.SnapshotPath,
                            row.SizeBytes, row.ChecksumSha256, row.DocumentVersion, null)) throw Changed();
                    update = value => row.SnapshotPath = value;
                    folder = "signatures";
                }
                else if (reference.SourceKind == "esign-artifact")
                {
                    if (!evidence.TryGetValue(reference.SourceId, out var row)
                        || reference != new DatabaseBackupStoredFileReference("esign-artifact", row.Id, row.StoragePath,
                            row.SizeBytes, row.ChecksumSha256, null, row.EnvelopeId)) throw Changed();
                    update = value => row.StoragePath = value;
                    folder = "signatures";
                }
                else if (reference.SourceKind == "profile-photo")
                {
                    if (!users.TryGetValue(reference.SourceId, out var row) || row.PhotoUrl != reference.StoredPath) throw Changed();
                    if (!binding.ExternalReference)
                    {
                        update = value => row.PhotoUrl = UserPhotoStoragePaths.RequestPath + "/" + value;
                        folder = "photos";
                    }
                }
                if (binding.ExternalReference) continue; // Never fetch external photographs.
                var sourcePath = CheckedPath(binding.ArchiveEntryPath!);
                await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                Stream destination = Stream.Null;
                FileStream? ownedOutput = null;
                string? relative = null;
                try
                {
                    if (update is not null)
                    {
                        var extension = Path.GetExtension(binding.ArchiveEntryPath!);
                        if (extension.Length is < 2 or > 12 || extension[1..].Any(character => !char.IsAsciiLetterOrDigit(character))) extension = ".bin";
                        relative = reference.SourceKind + "-" + reference.SourceId + extension;
                        var directory = Path.Combine(runtimeRoot, folder!);
                        Directory.CreateDirectory(directory);
                        EnsureOutputDirectory(directory);
                        ownedOutput = new FileStream(Path.Combine(directory, relative), FileMode.CreateNew,
                            FileAccess.Write, FileShare.None, 81920, true);
                        destination = ownedOutput;
                    }
                    var actual = await DatabaseBackupArchiveHash.CopyAsync(input, destination, token);
                    if (actual.SizeBytes != binding.ActualSizeBytes || !string.Equals(actual.Sha256, binding.ActualSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Восстановленный обычный документ изменился.");
                }
                finally { if (ownedOutput is not null) await ownedOutput.DisposeAsync(); }
                if (update is not null) update(relative!);
            }
            await db.SaveChangesAsync(token);
            db.ChangeTracker.Clear();
        }

        int[] Ids(string kind) => batch.Where(binding => binding.Reference.SourceKind == kind).Select(binding => binding.Reference.SourceId).ToArray();
        static InvalidDataException Changed() => new("Файловая ссылка восстановленной базы изменилась.");
        static void EnsureOutputDirectory(string directory)
        {
            for (var current = Path.GetFullPath(directory); current is not null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Папка подготовки восстановленных документов содержит файловую ссылку.");
        }
        string CheckedPath(string entry)
        {
            DatabaseBackupNativeBindings.ValidatePortablePath(entry);
            var root = Path.GetFullPath(filesRoot).TrimEnd(Path.DirectorySeparatorChar);
            var path = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw Changed();
            for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Changed();
                if (string.Equals(current, root, comparison)) break;
            }
            return path;
        }
    }

    public static async Task<long> CalculateRequiredBytesAsync(Stream mapping, string inventorySha, CancellationToken token)
    {
        long total = CapacityReserveBytes;
        await foreach (var binding in DatabaseBackupNativeBindings.ReadAsync(mapping, inventorySha, true, token))
        {
            if (binding.ExternalReference || binding.Reference.SourceKind is not ("esign-original" or "esign-artifact" or "profile-photo"))
                continue;
            try { total = checked(total + binding.ActualSizeBytes!.Value); }
            catch (OverflowException error)
            { throw new InvalidDataException("Объём подготовки восстановленных документов превышает допустимый размер.", error); }
        }
        return total;
    }

    private static long GetAvailableFreeBytes(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Select the longest mount point, not just '/' on Unix: restored data may
        // be on a different mounted volume. An unidentifiable volume fails closed.
        var selected = DriveInfo.GetDrives().Select(drive => new { Drive = drive, Root = Path.GetFullPath(drive.Name) })
            .Where(item => string.Equals(fullPath, item.Root, comparison)
                || fullPath.StartsWith(item.Root.EndsWith(Path.DirectorySeparatorChar)
                    ? item.Root : item.Root + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(item => item.Root.Length).FirstOrDefault();
        if (selected is null || !selected.Drive.IsReady)
            throw new IOException("Не удалось определить доступное место для подготовки восстановленных документов.");
        return selected.Drive.AvailableFreeSpace;
    }
}
