using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public static class LegacyStorageSources
{
    public const string TaskFiles = "task-files";
    public const string CaseDocuments = "case-documents";
    public const string InternalChat = "internal-chat";
    public const string MessengerSnapshots = "messenger-snapshots";
    public const string ElectronicSignatures = "electronic-signatures";
    public const string ProfilePhotos = "profile-photos";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        TaskFiles, CaseDocuments, InternalChat, MessengerSnapshots, ElectronicSignatures, ProfilePhotos,
    };
}

public sealed record LegacyStorageBackfillRequest(
    string Source,
    long AfterId = 0,
    int BatchSize = 100,
    bool Apply = false);

public sealed record LegacyStorageBackfillItem(
    long SourceId,
    string OwnerType,
    string OwnerId,
    string LogicalName,
    string Status,
    long SizeBytes,
    string? Sha256,
    string? Locator,
    Guid? ObjectId,
    string? Detail);

public sealed record LegacyStorageBackfillReport(
    string Source,
    bool Applied,
    long AfterId,
    long? NextAfterId,
    bool HasMore,
    int Scanned,
    int Registered,
    int AlreadyRegistered,
    int Missing,
    int Invalid,
    long BytesVerified,
    IReadOnlyList<LegacyStorageBackfillItem> Items);

public interface ILegacyStorageCatalogBackfillService
{
    Task<LegacyStorageBackfillReport> RunAsync(LegacyStorageBackfillRequest request, CancellationToken cancellationToken);
}

public sealed class LegacyStorageCatalogBackfillService(
    PaymentContext db,
    ILegacyLocalStorageLocator locator,
    IWebHostEnvironment environment,
    IConfiguration configuration) : ILegacyStorageCatalogBackfillService
{
    public async Task<LegacyStorageBackfillReport> RunAsync(
        LegacyStorageBackfillRequest request,
        CancellationToken cancellationToken)
    {
        var source = (request.Source ?? string.Empty).Trim().ToLowerInvariant();
        if (!LegacyStorageSources.All.Contains(source))
            throw new ArgumentException($"Unknown legacy storage source '{request.Source}'.", nameof(request));
        var batchSize = Math.Clamp(request.BatchSize, 1, 500);
        var candidates = await LoadCandidatesAsync(source, Math.Max(0, request.AfterId), batchSize + 1, cancellationToken);
        var hasMore = candidates.Count > batchSize;
        if (hasMore) candidates.RemoveAt(candidates.Count - 1);

        var items = new List<LegacyStorageBackfillItem>(candidates.Count);
        var registered = 0;
        var existingCount = 0;
        var missing = 0;
        var invalid = 0;
        long verifiedBytes = 0;
        var objectIds = candidates.ToDictionary(
            candidate => candidate.SourceId,
            candidate => DeterministicGuid($"legacy:{source}:{candidate.SourceId}"));
        var candidateIds = objectIds.Values.ToArray();
        var existingObjectIds = candidateIds.Length == 0
            ? new HashSet<Guid>()
            : (await db.StorageObjects.AsNoTracking()
                .Where(item => candidateIds.Contains(item.Id))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken))
                .ToHashSet();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath;
            string legacyLocator;
            try
            {
                fullPath = ResolveCandidatePath(candidate);
                if (!locator.TryCreateLocator(fullPath, out legacyLocator))
                    throw new InvalidOperationException("The file is outside configured persistent storage roots.");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                invalid++;
                items.Add(ToItem(candidate, "invalid", null, null, null, ex.Message));
                continue;
            }

            if (!File.Exists(fullPath))
            {
                missing++;
                items.Add(ToItem(candidate, "missing", legacyLocator, null, null, "File is missing from legacy storage."));
                continue;
            }

            var (size, sha256) = await HashFileAsync(fullPath, cancellationToken);
            verifiedBytes += size;
            var objectId = objectIds[candidate.SourceId];
            if (existingObjectIds.Contains(objectId))
            {
                existingCount++;
                items.Add(ToItem(candidate, "alreadyRegistered", legacyLocator, objectId, sha256,
                    candidate.ExpectedSize is > 0 && candidate.ExpectedSize != size ? "Stored size differs from the file size." : null, size));
                continue;
            }

            var sizeDetail = candidate.ExpectedSize is > 0 && candidate.ExpectedSize != size
                ? $"Stored size {candidate.ExpectedSize} differs from actual size {size}."
                : null;
            if (request.Apply)
            {
                AddCatalogEntry(candidate, objectId, legacyLocator, size, sha256);
                registered++;
            }
            items.Add(ToItem(candidate, request.Apply ? "registered" : "ready", legacyLocator, objectId, sha256, sizeDetail, size));
        }

        if (request.Apply && registered > 0)
            await db.SaveChangesAsync(cancellationToken);

        return new LegacyStorageBackfillReport(
            source,
            request.Apply,
            request.AfterId,
            candidates.Count == 0 ? null : candidates[^1].SourceId,
            hasMore,
            candidates.Count,
            registered,
            existingCount,
            missing,
            invalid,
            verifiedBytes,
            items);
    }

    private async Task<List<LegacyCandidate>> LoadCandidatesAsync(
        string source,
        long afterId,
        int take,
        CancellationToken cancellationToken)
        => source switch
        {
            LegacyStorageSources.TaskFiles => await db.TaskJournalFiles.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id > afterId).OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "task-file", item.Id.ToString(), item.OriginalFileName,
                    item.ContentType, item.SizeBytes, item.StoragePath, null, item.UploadedByUserId, item.UploadedAt))
                .ToListAsync(cancellationToken),
            LegacyStorageSources.CaseDocuments => await db.ClientCaseDocuments.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id > afterId).OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "case-document", item.Id.ToString(), item.OriginalFileName,
                    item.ContentType, item.SizeBytes, item.StoragePath, null, item.UploadedByUserId, item.UploadedAt))
                .ToListAsync(cancellationToken),
            LegacyStorageSources.InternalChat => await db.InternalChatAttachments.AsNoTracking()
                .Where(item => item.Id > afterId).OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "chat-attachment", item.Id.ToString(), item.FileName,
                    item.ContentType, item.Size, item.StoredFileName, item.Message.ThreadId, item.UploadedByUserId, item.CreatedAt))
                .ToListAsync(cancellationToken),
            LegacyStorageSources.MessengerSnapshots => await db.MessengerDocumentDeliveries.AsNoTracking()
                .Where(item => item.Id > afterId).OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "messenger-delivery", item.Id.ToString(), item.OriginalFileName,
                    item.ContentType, item.SizeBytes, item.SnapshotPath, null, item.CreatedByUserId, item.CreatedAt))
                .ToListAsync(cancellationToken),
            LegacyStorageSources.ElectronicSignatures => await db.ElectronicSignatureArtifacts.AsNoTracking()
                .Where(item => item.Id > afterId).OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "esign-artifact", item.Id.ToString(), item.FileName,
                    item.ContentType, item.SizeBytes, item.StoragePath, null, null, item.CreatedAt))
                .ToListAsync(cancellationToken),
            LegacyStorageSources.ProfilePhotos => await db.Users.AsNoTracking()
                .Where(item => item.Id > afterId && item.PhotoUrl != null && item.PhotoUrl != "")
                .OrderBy(item => item.Id).Take(take)
                .Select(item => new LegacyCandidate(item.Id, "profile-photo", item.Id.ToString(), item.PhotoUrl!,
                    "image/*", null, item.PhotoUrl!, null, item.Id, item.UpdatedAt))
                .ToListAsync(cancellationToken),
            _ => throw new InvalidOperationException("Unsupported legacy storage source."),
        };

    private string ResolveCandidatePath(LegacyCandidate item)
    {
        var fileRoot = ApplicationFileStoragePaths.ResolveRoot(environment, configuration);
        return item.OwnerType switch
        {
            "task-file" => SafeCombine(Path.Combine(fileRoot, "task-files"), item.StoredPath),
            "case-document" => SafeCombine(Path.Combine(fileRoot, "case-documents"), item.StoredPath),
            "chat-attachment" => SafeCombine(Path.Combine(fileRoot, "internal-chat-files"),
                Path.Combine(item.ParentId!.Value.ToString(), Path.GetFileName(item.StoredPath))),
            "messenger-delivery" => SafeCombine(Path.Combine(fileRoot, "messenger-document-deliveries"), item.StoredPath),
            "esign-artifact" => ResolveElectronicSignaturePath(item.StoredPath, fileRoot),
            "profile-photo" => ResolveProfilePhotoPath(item.StoredPath),
            _ => throw new InvalidOperationException("Unsupported legacy owner type."),
        };
    }

    private string ResolveElectronicSignaturePath(string value, string fileRoot)
    {
        if (Path.IsPathRooted(value)) return Path.GetFullPath(value);
        var configured = configuration["ElectronicSignature:StorageRoot"];
        var root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(fileRoot, "electronic-signatures")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        return SafeCombine(root, value);
    }

    private string ResolveProfilePhotoPath(string value)
    {
        var currentRoot = UserPhotoStoragePaths.ResolveRoot(environment, configuration);
        var current = UserPhotoStoragePaths.ResolvePhotoPath(currentRoot, value);
        if (current is not null && File.Exists(current)) return current;
        var legacy = UserPhotoStoragePaths.ResolvePhotoPath(UserPhotoStoragePaths.ResolveLegacyRoot(environment), value);
        return legacy ?? current ?? throw new InvalidOperationException("Profile photo URL cannot be mapped to a local file.");
    }

    private void AddCatalogEntry(
        LegacyCandidate candidate,
        Guid objectId,
        string legacyLocator,
        long size,
        string sha256)
    {
        var generationId = DeterministicGuid($"legacy-generation:{candidate.OwnerType}:{candidate.SourceId}");
        var now = DateTime.UtcNow;
        var storageObject = new StorageObject
        {
            Id = objectId,
            DataClass = candidate.OwnerType,
            OwnerType = candidate.OwnerType,
            OwnerId = candidate.OwnerId,
            LogicalName = Path.GetFileName(candidate.LogicalName),
            ContentType = string.IsNullOrWhiteSpace(candidate.ContentType) ? "application/octet-stream" : candidate.ContentType,
            CurrentGeneration = 1,
            Version = 1,
            CreatedAt = candidate.CreatedAt,
            UpdatedAt = now,
        };
        var generation = new StorageObjectGeneration
        {
            Id = generationId,
            StorageObject = storageObject,
            Generation = 1,
            SizeBytes = size,
            Sha256 = sha256,
            PolicyVersion = "legacy-v1",
            CreatedByUserId = candidate.CreatedByUserId,
            CreatedAt = candidate.CreatedAt,
        };
        generation.Replicas.Add(new StorageReplica
        {
            StorageObjectGeneration = generation,
            ProviderKey = "legacy-local",
            Locator = legacyLocator,
            Status = StorageReplicaStatuses.Available,
            SizeBytes = size,
            Sha256 = sha256,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        storageObject.Generations.Add(generation);
        db.StorageObjects.Add(storageObject);
    }

    private static LegacyStorageBackfillItem ToItem(
        LegacyCandidate candidate,
        string status,
        string? locator,
        Guid? objectId,
        string? sha256,
        string? detail,
        long? actualSize = null)
        => new(candidate.SourceId, candidate.OwnerType, candidate.OwnerId, Path.GetFileName(candidate.LogicalName),
            status, actualSize ?? candidate.ExpectedSize ?? 0, sha256, locator, objectId, detail);

    private static async Task<(long Size, string Sha256)> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return (stream.Length, Convert.ToHexString(hash).ToLowerInvariant());
    }

    private static string SafeCombine(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidOperationException("An absolute legacy path is not allowed for this data class.");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(path, normalizedRoot, comparison)
            && !path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Legacy path escapes its configured storage root.");
        return path;
    }

    private static Guid DeterministicGuid(string value)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed record LegacyCandidate(
        long SourceId,
        string OwnerType,
        string OwnerId,
        string LogicalName,
        string ContentType,
        long? ExpectedSize,
        string StoredPath,
        int? ParentId,
        int? CreatedByUserId,
        DateTime CreatedAt);
}
