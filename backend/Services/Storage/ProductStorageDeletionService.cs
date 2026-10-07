using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public interface IProductStorageDeletionService
{
    // Authorize the original domain row first. Caller owns the transaction and
    // removes the domain row/history atomically with this logical tombstone.
    Task<string> DeleteAsync(string source, long fileId, string name, string contentType,
        string compatibilityPath, int? actorId, CancellationToken ct);
}

public sealed class ProductStorageDeletionService(PaymentContext db, IStorageObjectService storage,
    IStorageProviderRegistry providers, ILegacyLocalStorageLocator locator) : IProductStorageDeletionService
{
    public async Task<string> DeleteAsync(string source, long fileId, string name, string contentType,
        string compatibilityPath, int? actorId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Deletion requires the domain transaction.");
        if (fileId <= 0) throw new ArgumentOutOfRangeException(nameof(fileId));
        var dataClass = LegacyStorageSources.GetDataClass(source);
        var binding = await db.StorageProductBindings.AsNoTracking().Where(b => b.Source == source && b.SourceId == fileId)
            .Select(b => (Guid?)b.StorageObjectId).SingleOrDefaultAsync(ct);
        var objectId = binding ?? LegacyStorageSources.GetObjectId(source, fileId);
        var item = await db.StorageObjects.AsNoTracking().SingleOrDefaultAsync(o => o.Id == objectId, ct);
        if (item is null) return binding.HasValue ? "unavailable" : "legacy";
        if (item.DataClass != dataClass || item.OwnerType != dataClass || item.OwnerId != fileId.ToString(CultureInfo.InvariantCulture)
            || item.LogicalName != Path.GetFileName(name)
            || item.ContentType != (string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType)) return "conflict";

        // Compatibility bytes are a separate physical copy, not a reason to
        // bypass retention. Register them so the existing deletion worker and
        // tombstone/legal-hold policy cover them as well as managed replicas.
        if (!locator.TryCreateLocator(compatibilityPath, out var compatibilityLocator)
            || !providers.TryGet("legacy-local", out var legacy)) return "unavailable";
        var generation = await db.StorageObjectGenerations.AsNoTracking()
            .SingleOrDefaultAsync(g => g.StorageObjectId == objectId && g.Generation == item.CurrentGeneration, ct);
        if (generation is null) return "unavailable";
        var metadata = await legacy.HeadAsync(compatibilityLocator, ct);
        if (metadata.Status != StorageOperationStatus.NotFound)
        {
            if (metadata.Status != StorageOperationStatus.Succeeded) return "unavailable";
            if (metadata.SizeBytes != generation.SizeBytes
                || !string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase)) return "conflict";
            if (binding.HasValue && objectId != LegacyStorageSources.GetObjectId(source, fileId)
                && !await db.StorageReplicas.AnyAsync(r => r.StorageObjectGenerationId == generation.Id
                    && r.Locator == compatibilityLocator && (r.ProviderKey == "legacy-local" || r.ProviderKey == "managed-compatibility"), ct))
            {
                if (!providers.TryGet("managed-compatibility", out _)) return "unavailable";
                db.StorageReplicas.Add(new()
                {
                    StorageObjectGenerationId = generation.Id,
                    ProviderKey = "managed-compatibility",
                    Locator = compatibilityLocator,
                    Status = StorageReplicaStatuses.Available,
                    SizeBytes = metadata.SizeBytes,
                    Sha256 = metadata.Sha256,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastVerifiedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }
        }
        return await storage.DeleteAsync(objectId, actorId, "Удаление вложения CRM", ct) ? "deleted" : "unavailable";
    }
}
