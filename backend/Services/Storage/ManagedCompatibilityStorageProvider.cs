using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

// This is NOT permission to delete original legacy files. Only explicit copies
// registered by the managed product deletion transaction use this provider key.
public sealed class ManagedCompatibilityStorageProvider(PaymentContext db, LegacyLocalStorageProvider legacy) : IStorageProvider
{
    public string Key => "managed-compatibility";
    public StorageProviderCapabilities Capabilities => legacy.Capabilities;
    public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct) => legacy.PutAsync(request, ct);
    public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct) => legacy.HeadAsync(locator, ct);
    public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct) => legacy.OpenReadAsync(locator, ct);
    public async Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct)
    {
        var matches = await db.StorageReplicas.AsNoTracking().Where(r => r.ProviderKey == Key && r.Locator == locator
                && r.StorageObjectGeneration.StorageObject.IsDeleted)
            .Select(r => new { r.StorageObjectGeneration.StorageObjectId, r.StorageObjectGeneration.SizeBytes, r.StorageObjectGeneration.Sha256 })
            .Take(2).ToListAsync(ct);
        if (matches.Count != 1) return new(StorageOperationStatus.Conflict, locator, "No unique managed deletion authority.");
        var copy = matches[0];
        var tombstone = await db.StorageTombstones.AsNoTracking().Where(t => t.StorageObjectId == copy.StorageObjectId)
            .OrderByDescending(t => t.DeletedAt).FirstOrDefaultAsync(ct);
        if (tombstone is null || tombstone.LegalHold || tombstone.RetainUntil > DateTime.UtcNow
            || await db.StorageReplicas.AsNoTracking().AnyAsync(r => r.Locator == locator
                && (r.ProviderKey == "legacy-local" || !r.StorageObjectGeneration.StorageObject.IsDeleted), ct))
            return new(StorageOperationStatus.Conflict, locator, "Copy is retained or shared with protected legacy data.");
        var metadata = await legacy.HeadAsync(locator, ct);
        if (metadata.Status == StorageOperationStatus.NotFound) return new(StorageOperationStatus.NotFound, locator);
        if (metadata.Status != StorageOperationStatus.Succeeded) return new(metadata.Status, locator, metadata.Error);
        if (metadata.SizeBytes != copy.SizeBytes || !string.Equals(metadata.Sha256, copy.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(StorageOperationStatus.Conflict, locator, "Compatibility copy changed after registration.");
        ct.ThrowIfCancellationRequested();
        try
        {
            File.Delete(legacy.ResolveValidatedPath(locator));
            return new(StorageOperationStatus.Succeeded, locator);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StorageOperationStatus.Unavailable, locator, "Compatibility copy could not be removed.");
        }
    }
}
