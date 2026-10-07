using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

public enum ProductStorageReadStatus { Legacy, Available, Unavailable, Deleted }

public sealed record ProductStorageReadResult(ProductStorageReadStatus Status, StorageObjectReadResult? File = null);

// Call only after authorizing the original product row. Catalog ids do not grant access.
public interface IProductStorageReader
{
    Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
        string contentType, CancellationToken cancellationToken);
}

public sealed class ProductStorageReader(
    PaymentContext db,
    IStorageObjectService storage,
    IOptionsMonitor<StorageOrchestrationOptions> options) : IProductStorageReader
{
    public async Task<ProductStorageReadResult> OpenReadAsync(string source, long fileId, string logicalName,
        string contentType, CancellationToken cancellationToken)
    {
        var dataClass = LegacyStorageSources.GetDataClass(source);
        if (fileId <= 0)
            throw new ArgumentException("Unknown storage source or invalid file id.");
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.ProductReadEnabledClasses.Contains(dataClass, StringComparer.Ordinal))
            return new(ProductStorageReadStatus.Legacy);

        var binding = await db.StorageProductBindings.AsNoTracking()
            .Where(item => item.Source == source && item.SourceId == fileId)
            .Select(item => new { item.StorageObjectId, item.Version }).SingleOrDefaultAsync(cancellationToken);
        var boundObjectId = binding?.StorageObjectId;
        var objectId = boundObjectId ?? LegacyStorageSources.GetObjectId(source, fileId);
        var entry = await db.StorageObjects.AsNoTracking().Where(item => item.Id == objectId)
            .Select(item => new { item.DataClass, item.OwnerType, item.OwnerId, item.LogicalName, item.ContentType, item.IsDeleted, item.Version })
            .SingleOrDefaultAsync(cancellationToken);
        // Unregistered legacy rows keep their old resolver. A registered row must never bypass verification.
        if (entry is null) return new(boundObjectId.HasValue ? ProductStorageReadStatus.Unavailable : ProductStorageReadStatus.Legacy);
        if (entry.IsDeleted) return new(ProductStorageReadStatus.Deleted);
        var expectedName = Path.GetFileName(logicalName);
        var expectedContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
        var expectedOwnerId = fileId.ToString(CultureInfo.InvariantCulture);
        if (entry.DataClass != dataClass || entry.OwnerType != dataClass
            || entry.OwnerId != expectedOwnerId
            || entry.LogicalName != expectedName || entry.ContentType != expectedContentType)
            return new(ProductStorageReadStatus.Unavailable);

        var file = await storage.OpenReadAsync(objectId, cancellationToken);
        if (file is null) return new(ProductStorageReadStatus.Unavailable);
        var handedOff = false;
        try
        {
            if (file.ObjectId != objectId || file.LogicalName != expectedName || file.ContentType != expectedContentType)
                return new(ProductStorageReadStatus.Unavailable);

            // Reading may take long enough for a product replacement or catalog change.
            // Revalidate the current pointer and identity together, without trusting tracked rows.
            var current = db.StorageObjects.AsNoTracking().Where(item => item.Id == objectId
                && !item.IsDeleted && item.Version == entry.Version && item.CurrentGeneration == file.Generation
                && item.DataClass == dataClass && item.OwnerType == dataClass && item.OwnerId == expectedOwnerId
                && item.LogicalName == expectedName && item.ContentType == expectedContentType
                && !db.StorageTombstones.Any(tombstone => tombstone.StorageObjectId == objectId));
            if (binding is null)
                current = current.Where(item => !db.StorageProductBindings.Any(pointer => pointer.Source == source && pointer.SourceId == fileId));
            else
            {
                var expectedBindingVersion = binding.Version;
                current = current.Where(item => db.StorageProductBindings.Any(pointer => pointer.Source == source
                    && pointer.SourceId == fileId && pointer.StorageObjectId == objectId && pointer.Version == expectedBindingVersion));
            }
            if (!await current.AnyAsync(cancellationToken))
                return new(ProductStorageReadStatus.Unavailable);
            handedOff = true;
            return new(ProductStorageReadStatus.Available, file);
        }
        finally
        {
            if (!handedOff) await file.DisposeAsync();
        }
    }
}
