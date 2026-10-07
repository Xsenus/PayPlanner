using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Server-only recovery capability. A pending original is not a product read or
// evidence that all required copies are ready; callers must check live authority.
public interface IStorageOriginalReadService : IStorageReservedReadService
{
    Task<StorageObjectReadResult?> OpenOriginalReadAsync(StorageObjectWriteRequest request, CancellationToken ct);
}

public sealed partial class StorageObjectService
{
    public async Task<StorageObjectReadResult?> OpenOriginalReadAsync(StorageObjectWriteRequest request, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Original recovery reads require provider I/O outside a database transaction.");
        ValidateRequest(request);
        if (request.ExpectedSizeBytes is not { } size || size <= 0 || request.ExpectedSha256 is not { Length: 64 } sha
            || !sha.All(Uri.IsHexDigit) || request.ProductContextJson is not { Length: <= 65536 }) return null;
        var key = GetCommitOperationKey(request.IdempotencyKey);
        if (originalReadStages is null) return null;
        if (!await db.StorageOperations.AsNoTracking().AnyAsync(item => item.IdempotencyKey == key, ct))
            return await OpenReservedReadCoreAsync(request, true, ct);
        bool Enabled()
        {
            var policy = policies.Evaluate(request.DataClass);
            return options.CurrentValue.UploadCapacityChecksEnabled && policy.RoutingEnabled && policy.IsValid;
        }
        if (!Enabled()) return null;
        var candidate = await OriginalCandidates(request, key).FirstOrDefaultAsync(ct);
        if (candidate is null || !providers.TryGet("local", out var local)
            || providerHealth is not null && !providerHealth.CanAttempt("local")) return null;
        FileStream? verified = null;
        var handedOff = false;
        try
        {
            verified = await originalReadStages.VerifyAsync(key, candidate.Locator, size, sha,
                token => local.OpenReadAsync(candidate.Locator, token), ct);
            // The complete catalog proof and exact replica identity must survive I/O.
            if (verified is null || !Enabled()
                || await OriginalCandidates(request, key).FirstOrDefaultAsync(ct) != candidate) return null;
            handedOff = true;
            return new(candidate.ObjectId, candidate.Generation, request.ContentType, request.LogicalName,
                size, verified, "local", sha);
        }
        catch (Exception error) when (error is IOException or HttpRequestException or VerifiedReadStagingException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        { return null; }
        finally
        {
            if (!handedOff && verified is not null) await verified.DisposeAsync();
        }
    }

    private IQueryable<OriginalReadCandidate> OriginalCandidates(StorageObjectWriteRequest request, string key)
        => db.StorageReplicas.AsNoTracking().Where(replica => replica.ProviderKey == "local" && replica.ConnectionId == null
            && (replica.Status == StorageReplicaStatuses.Available || replica.Status == StorageReplicaStatuses.Pending)
            && replica.LastVerifiedAt != null && replica.SizeBytes == request.ExpectedSizeBytes && replica.Sha256 == request.ExpectedSha256
            && replica.StorageObjectGeneration.SizeBytes == request.ExpectedSizeBytes
            && replica.StorageObjectGeneration.Sha256 == request.ExpectedSha256
            && replica.StorageObjectGeneration.CreatedByUserId == request.CreatedByUserId
            && replica.StorageObjectGeneration.Generation == replica.StorageObjectGeneration.StorageObject.CurrentGeneration
            && !replica.StorageObjectGeneration.StorageObject.IsDeleted && !replica.StorageObjectGeneration.StorageObject.Tombstones.Any()
            && replica.StorageObjectGeneration.StorageObject.DataClass == request.DataClass
            && replica.StorageObjectGeneration.StorageObject.OwnerType == request.OwnerType
            && replica.StorageObjectGeneration.StorageObject.OwnerId == request.OwnerId
            && replica.StorageObjectGeneration.StorageObject.LogicalName == request.LogicalName
            && replica.StorageObjectGeneration.StorageObject.ContentType == request.ContentType
            && replica.StorageObjectGeneration.Operations.Any(operation => operation.IdempotencyKey == key
                && operation.OperationType == "commit" && operation.Status == StorageOperationStatuses.Succeeded
                && operation.StorageObjectId == replica.StorageObjectGeneration.StorageObjectId
                && operation.PayloadJson == request.ProductContextJson))
            .OrderBy(replica => replica.Id)
            .Select(replica => new OriginalReadCandidate(replica.StorageObjectGeneration.StorageObjectId,
                replica.StorageObjectGenerationId, replica.StorageObjectGeneration.Generation,
                replica.StorageObjectGeneration.StorageObject.Version, replica.Id, replica.Locator, replica.Status, replica.LastVerifiedAt));

    private sealed record OriginalReadCandidate(Guid ObjectId, Guid GenerationId, long Generation,
        long ObjectVersion, long ReplicaId, string Locator, string Status, DateTime? LastVerifiedAt);
}
