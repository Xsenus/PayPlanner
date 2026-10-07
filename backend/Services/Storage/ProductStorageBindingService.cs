using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record ProductStorageBindingRequest(string Source, long SourceId, Guid ObjectId,
    string LogicalName, string ContentType, long ExpectedVersion);
public sealed record ProductStorageBindingResult(bool Published, long? Version, string Status);
public sealed record PublishedProductStorageReceipt(string Source, long SourceId, string LogicalName,
    string ContentType, long SizeBytes, string Sha256, int? CreatedByUserId, string UploadKey,
    long ExpectedBindingVersion = 1);

// Read-only proof for recovery after a completed product owner handoff.
// The caller must already authorize the product row; catalog IDs grant no rights.
public interface IProductStoragePublishedReceiptVerifier
{
    Task<bool> VerifyPublishedReceiptAsync(PublishedProductStorageReceipt receipt, CancellationToken ct);
}

public interface IProductStorageBindingService
{
    // Caller authorizes the product row, then uses the SAME context/transaction for
    // domain changes and publication. Commit only after Published; rollback otherwise.
    Task<ProductStorageBindingResult> PublishAsync(ProductStorageBindingRequest request, CancellationToken ct);
}

public sealed class ProductStorageBindingService(PaymentContext db, IStoragePolicyService policies)
    : IProductStorageBindingService, IProductStoragePublishedReceiptVerifier
{
    public async Task<bool> VerifyPublishedReceiptAsync(PublishedProductStorageReceipt receipt, CancellationToken ct)
    {
        var dataClass = LegacyStorageSources.GetDataClass(receipt.Source);
        if (receipt.SourceId <= 0 || receipt.ExpectedBindingVersion <= 0 || receipt.SizeBytes <= 0
            || receipt.Sha256.Length != 64 || !receipt.Sha256.All(Uri.IsHexDigit)
            || string.IsNullOrWhiteSpace(receipt.UploadKey)) return false;
        var policy = policies.Evaluate(dataClass);
        if (!policy.RoutingEnabled || !policy.IsValid) return false;
        var name = Path.GetFileName(receipt.LogicalName);
        var type = string.IsNullOrWhiteSpace(receipt.ContentType) ? "application/octet-stream" : receipt.ContentType.Trim();
        var owner = receipt.SourceId.ToString(CultureInfo.InvariantCulture);
        var checksum = receipt.Sha256.ToLowerInvariant();
        var key = StorageObjectService.GetCommitOperationKey(receipt.UploadKey);
        var providers = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        var generationId = await (
            from binding in db.StorageProductBindings.AsNoTracking()
            join generation in db.StorageObjectGenerations.AsNoTracking() on binding.StorageObjectId equals generation.StorageObjectId
            where binding.Source == receipt.Source && binding.SourceId == receipt.SourceId
                && binding.Version == receipt.ExpectedBindingVersion
                && binding.StorageObject.DataClass == dataClass && binding.StorageObject.OwnerType == dataClass
                && binding.StorageObject.OwnerId == owner && binding.StorageObject.LogicalName == name
                && binding.StorageObject.ContentType == type && !binding.StorageObject.IsDeleted
                && binding.StorageObject.Version > 0 && binding.StorageObject.CurrentGeneration > 0
                && !db.StorageTombstones.Any(t => t.StorageObjectId == binding.StorageObjectId)
                && generation.Generation == binding.StorageObject.CurrentGeneration
                && generation.SizeBytes == receipt.SizeBytes && generation.Sha256 == checksum
                && generation.CreatedByUserId == receipt.CreatedByUserId
                && db.StorageOperations.Any(o => o.IdempotencyKey == key && o.OperationType == "commit"
                    && o.Status == StorageOperationStatuses.Succeeded && o.StorageObjectId == binding.StorageObjectId
                    && o.StorageObjectGenerationId == generation.Id)
                && !db.StorageOperations.Any(o => o.StorageObjectGenerationId == generation.Id
                    && o.OperationType == "commit" && o.Status != StorageOperationStatuses.Succeeded)
                && db.StorageReplicas.Where(r => r.StorageObjectGenerationId == generation.Id
                    && r.Status == StorageReplicaStatuses.Available && r.LastVerifiedAt != null
                    && r.SizeBytes == receipt.SizeBytes && r.Sha256 == checksum
                    && providers.Contains(r.ProviderKey.ToLower()))
                    .Select(r => r.ProviderKey.ToLower()).Distinct().Count() >= policy.RequiredCopies
            select (Guid?)generation.Id).SingleOrDefaultAsync(ct);
        if (generationId is null) return false;
        var current = policies.Evaluate(dataClass);
        return current.RoutingEnabled && current.IsValid
            && current.PolicyVersion == policy.PolicyVersion;
    }

    public async Task<ProductStorageBindingResult> PublishAsync(ProductStorageBindingRequest request, CancellationToken ct)
    {
        var dataClass = LegacyStorageSources.GetDataClass(request.Source);
        if (request.SourceId <= 0 || request.ObjectId == Guid.Empty || request.ExpectedVersion < 0
            || request.ExpectedVersion == long.MaxValue || string.IsNullOrWhiteSpace(request.LogicalName))
            throw new ArgumentException("Invalid product storage binding.");
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Публикация файла требует общей транзакции с записью CRM.");
        var policy = policies.Evaluate(dataClass);
        if (!policy.RoutingEnabled || !policy.IsValid) return new(false, null, "disabledOrInvalidPolicy");
        var entry = await db.StorageObjects.AsNoTracking().Where(o => o.Id == request.ObjectId
                && !db.StorageTombstones.Any(t => t.StorageObjectId == o.Id))
            .Select(o => new { o.DataClass, o.OwnerType, o.OwnerId, o.LogicalName, o.ContentType, o.IsDeleted, o.CurrentGeneration, o.Version })
            .SingleOrDefaultAsync(ct);
        var contentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType.Trim();
        if (entry is null || entry.IsDeleted || entry.DataClass != dataClass || entry.OwnerType != dataClass
            || entry.OwnerId != request.SourceId.ToString(CultureInfo.InvariantCulture)
            || entry.LogicalName != Path.GetFileName(request.LogicalName) || entry.ContentType != contentType)
            return new(false, null, "invalidObject");
        // Acquire a row write lock without changing metadata. A concurrent tombstone
        // or generation switch cannot race publication after this check.
        var locked = await db.StorageObjects.Where(o => o.Id == request.ObjectId && !o.IsDeleted
                && !db.StorageTombstones.Any(t => t.StorageObjectId == o.Id)
                && o.Version == entry.Version && o.CurrentGeneration == entry.CurrentGeneration)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Version, o => o.Version), ct);
        if (locked != 1) return new(false, null, "invalidObject");
        var generation = await db.StorageObjectGenerations.AsNoTracking()
            .Where(g => g.StorageObjectId == request.ObjectId && g.Generation == entry.CurrentGeneration)
            .Select(g => new { g.Id, g.SizeBytes, g.Sha256 }).SingleOrDefaultAsync(ct);
        if (generation is null || generation.SizeBytes < 0 || generation.Sha256.Length != 64
            || !generation.Sha256.All(Uri.IsHexDigit)) return new(false, null, "notReady");
        var hasUncertainCommit = await db.StorageOperations.AsNoTracking()
            .AnyAsync(o => o.StorageObjectGenerationId == generation.Id && o.OperationType == "commit"
                && o.Status != StorageOperationStatuses.Succeeded, ct);
        var writeProviders = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        var copies = await db.StorageReplicas.AsNoTracking()
            .Where(r => r.StorageObjectGenerationId == generation.Id && r.Status == StorageReplicaStatuses.Available
                && r.LastVerifiedAt != null
                && r.SizeBytes == generation.SizeBytes && r.Sha256 == generation.Sha256
                && writeProviders.Contains(r.ProviderKey.ToLower()))
            .Select(r => r.ProviderKey.ToLower()).Distinct().CountAsync(ct);
        if (hasUncertainCommit || copies < policy.RequiredCopies) return new(false, null, "notReady");
        var nextVersion = request.ExpectedVersion + 1;
        var now = DateTime.UtcNow;
        // Raw SQL bypasses the context's timestamp-without-time-zone converter.
        // Preserve the UTC wall clock rather than letting PostgreSQL apply session TZ.
        var storedTimestamp = DateTime.SpecifyKind(now, DateTimeKind.Unspecified);
        object timestampParameter = db.Database.IsNpgsql()
            ? new NpgsqlParameter("publishedAt", NpgsqlDbType.Timestamp) { Value = storedTimestamp }
            : storedTimestamp;
        int changed;
        if (request.ExpectedVersion == 0)
        {
            // Both supported providers support ON CONFLICT; a losing insert does not
            // abort the PostgreSQL transaction or require swallowing constraint errors.
            changed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "StorageProductBindings" ("Source", "SourceId", "StorageObjectId", "Version", "UpdatedAt")
                VALUES ({request.Source}, {request.SourceId}, {request.ObjectId}, {nextVersion}, {timestampParameter})
                ON CONFLICT ("Source", "SourceId") DO NOTHING
                """, ct);
        }
        else
        {
            changed = await db.StorageProductBindings.Where(b => b.Source == request.Source && b.SourceId == request.SourceId
                    && b.Version == request.ExpectedVersion)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.StorageObjectId, request.ObjectId)
                    .SetProperty(b => b.Version, nextVersion).SetProperty(b => b.UpdatedAt, now), ct);
        }
        if (changed == 1) return new(true, nextVersion, "published");
        var current = await db.StorageProductBindings.AsNoTracking()
            .Where(b => b.Source == request.Source && b.SourceId == request.SourceId)
            .Select(b => new { b.StorageObjectId, b.Version }).SingleOrDefaultAsync(ct);
        return current is not null && current.StorageObjectId == request.ObjectId && current.Version == nextVersion
            ? new(true, nextVersion, "alreadyPublished") : new(false, current?.Version, "conflict");
    }
}
