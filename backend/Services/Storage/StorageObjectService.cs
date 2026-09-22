using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageObjectWriteRequest(
    string DataClass,
    string OwnerType,
    string OwnerId,
    string LogicalName,
    string ContentType,
    Stream Content,
    string IdempotencyKey,
    int? CreatedByUserId = null);

public sealed record StorageObjectWriteResult(
    Guid ObjectId,
    long Generation,
    string Status,
    int AvailableCopies,
    int RequiredCopies,
    string PolicyVersion,
    string? Error = null);

public sealed record StorageObjectReadResult(
    Guid ObjectId,
    long Generation,
    string ContentType,
    string LogicalName,
    long SizeBytes,
    Stream Content,
    string ProviderKey) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

public interface IStorageObjectService
{
    Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken);
    Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken);
}

public sealed class StorageObjectService(
    PaymentContext db,
    IStoragePolicyService policies,
    IStorageProviderRegistry providers,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    IStorageProviderHealthTracker? providerHealth = null) : IStorageObjectService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StorageObjectWriteResult> WriteAsync(StorageObjectWriteRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var policy = policies.Evaluate(request.DataClass);
        if (!policy.RoutingEnabled)
            return new(Guid.Empty, 0, "disabled", 0, policy.RequiredCopies, policy.PolicyVersion, "Storage routing is disabled.");
        if (!policy.IsValid)
            return new(Guid.Empty, 0, "invalidPolicy", 0, policy.RequiredCopies, policy.PolicyVersion, string.Join(" ", policy.Errors));

        var normalizedIdempotency = request.IdempotencyKey.Trim();
        var operationKey = BuildOperationKey(normalizedIdempotency, "commit");
        var existing = await db.StorageOperations.AsNoTracking()
            .Where(item => item.IdempotencyKey == operationKey)
            .Select(item => new { item.StorageObjectId, item.StorageObjectGenerationId, item.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            var generationNumber = existing.StorageObjectGenerationId is { } generationId
                ? await db.StorageObjectGenerations.Where(item => item.Id == generationId).Select(item => item.Generation).SingleAsync(cancellationToken)
                : 0;
            var copies = existing.StorageObjectGenerationId is { } id
                ? await db.StorageReplicas.CountAsync(item => item.StorageObjectGenerationId == id && item.Status == StorageReplicaStatuses.Available, cancellationToken)
                : 0;
            return new(existing.StorageObjectId, generationNumber, existing.Status == StorageOperationStatuses.Succeeded ? "available" : "pending",
                copies, policy.RequiredCopies, policy.PolicyVersion);
        }

        var objectId = Guid.NewGuid();
        const long generationNumberNew = 1;
        var safeName = SanitizeName(request.LogicalName);
        var relativeLocator = $"{SanitizeSegment(request.DataClass)}/{objectId:N}/{generationNumberNew}/{safeName}";
        var local = providers.GetRequired("local");
        if (providerHealth is not null && !providerHealth.CanAttempt(local.Key))
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, "Local storage circuit is temporarily open.");
        var localWrite = await local.PutAsync(new StorageWriteRequest(
            relativeLocator, request.Content, request.ContentType, Overwrite: false), cancellationToken);
        providerHealth?.Record(local.Key, localWrite.Status, localWrite.Error);
        if (localWrite.Status != StorageOperationStatus.Succeeded)
            return new(Guid.Empty, 0, "failed", 0, policy.RequiredCopies, policy.PolicyVersion, localWrite.Error ?? "Local durable staging failed.");

        var now = DateTime.UtcNow;
        var storageObject = new StorageObject
        {
            Id = objectId,
            DataClass = request.DataClass.Trim(),
            OwnerType = request.OwnerType.Trim(),
            OwnerId = request.OwnerId.Trim(),
            LogicalName = request.LogicalName.Trim(),
            ContentType = string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType.Trim(),
            CurrentGeneration = generationNumberNew,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var generation = new StorageObjectGeneration
        {
            StorageObject = storageObject,
            Generation = generationNumberNew,
            SizeBytes = localWrite.SizeBytes,
            Sha256 = localWrite.Sha256!,
            PolicyVersion = policy.PolicyVersion,
            CreatedByUserId = request.CreatedByUserId,
            CreatedAt = now,
        };
        storageObject.Generations.Add(generation);
        var localIsPolicyCopy = policy.WriteProviders.Contains("local", StringComparer.OrdinalIgnoreCase);
        generation.Replicas.Add(new StorageReplica
        {
            ProviderKey = "local",
            Locator = relativeLocator,
            Status = localIsPolicyCopy ? StorageReplicaStatuses.Available : StorageReplicaStatuses.Pending,
            SizeBytes = localWrite.SizeBytes,
            Sha256 = localWrite.Sha256,
            LastVerifiedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        storageObject.Operations.Add(new StorageOperation
        {
            StorageObjectGeneration = generation,
            OperationType = "commit",
            Status = StorageOperationStatuses.Succeeded,
            IdempotencyKey = operationKey,
            ProviderKey = "local",
            AttemptCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now,
        });

        foreach (var providerKey in policy.WriteProviders.Where(key => !string.Equals(key, "local", StringComparison.OrdinalIgnoreCase)))
        {
            var targetLocator = BuildConnectedLocator(providerKey, relativeLocator);
            generation.Replicas.Add(new StorageReplica
            {
                ProviderKey = providerKey,
                ConnectionId = options.CurrentValue.ProviderConnections.GetValueOrDefault(providerKey),
                Locator = targetLocator,
                Status = StorageReplicaStatuses.Pending,
                SizeBytes = localWrite.SizeBytes,
                Sha256 = localWrite.Sha256,
                CreatedAt = now,
                UpdatedAt = now,
            });
            storageObject.Operations.Add(new StorageOperation
            {
                StorageObjectGeneration = generation,
                OperationType = StorageOperationTypes.Replicate,
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = BuildOperationKey(normalizedIdempotency, providerKey),
                ProviderKey = providerKey,
                ConnectionId = options.CurrentValue.ProviderConnections.GetValueOrDefault(providerKey),
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(targetLocator, "local", relativeLocator), JsonOptions),
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        try
        {
            db.StorageObjects.Add(storageObject);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await local.DeleteAsync(relativeLocator, CancellationToken.None);
            throw;
        }

        var available = localIsPolicyCopy ? 1 : 0;
        return new(objectId, generationNumberNew,
            available >= policy.RequiredCopies ? "available" : "pending",
            available, policy.RequiredCopies, policy.PolicyVersion);
    }

    public async Task<StorageObjectReadResult?> OpenReadAsync(Guid objectId, CancellationToken cancellationToken)
    {
        var storageObject = await db.StorageObjects.AsNoTracking().SingleOrDefaultAsync(item => item.Id == objectId, cancellationToken);
        if (storageObject is null || storageObject.IsDeleted) return null;
        var generation = await db.StorageObjectGenerations.AsNoTracking()
            .Include(item => item.Replicas)
            .SingleAsync(item => item.StorageObjectId == objectId && item.Generation == storageObject.CurrentGeneration, cancellationToken);
        var policy = policies.Evaluate(storageObject.DataClass);
        var ranking = policy.ReadProviders.Select((key, index) => (key, index))
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);
        var replicas = generation.Replicas
            .Where(item => item.Status == StorageReplicaStatuses.Available)
            .OrderBy(item => ranking.GetValueOrDefault(item.ProviderKey, int.MaxValue))
            .ThenByDescending(item => item.LastVerifiedAt)
            .ToArray();
        foreach (var replica in replicas)
        {
            if (!providers.TryGet(replica.ProviderKey, out var provider)) continue;
            if (providerHealth is not null && !providerHealth.CanAttempt(replica.ProviderKey)) continue;
            var metadata = await provider.HeadAsync(replica.Locator, cancellationToken);
            providerHealth?.Record(replica.ProviderKey, metadata.Status, metadata.Error);
            if (metadata.Status != StorageOperationStatus.Succeeded
                || metadata.SizeBytes != generation.SizeBytes
                || !string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await MarkReplicaSuspectAsync(replica.Id, metadata.Error ?? "Replica validation failed.", cancellationToken);
                continue;
            }
            var read = await provider.OpenReadAsync(replica.Locator, cancellationToken);
            providerHealth?.Record(replica.ProviderKey, read.Status, read.Error);
            if (read.Status == StorageOperationStatus.Succeeded && read.Content is not null)
                return new(objectId, generation.Generation, storageObject.ContentType, storageObject.LogicalName,
                    generation.SizeBytes, read.Content, replica.ProviderKey);
            await MarkReplicaSuspectAsync(replica.Id, read.Error ?? "Replica read failed.", cancellationToken);
        }
        return null;
    }

    public async Task<bool> DeleteAsync(Guid objectId, int? deletedByUserId, string? reason, CancellationToken cancellationToken)
    {
        var objectDataClass = await db.StorageObjects.AsNoTracking()
            .Where(item => item.Id == objectId)
            .Select(item => item.DataClass)
            .SingleOrDefaultAsync(cancellationToken);
        if (objectDataClass is null) return false;
        var policy = policies.Evaluate(objectDataClass);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var updated = await db.StorageObjects
            .Where(item => item.Id == objectId && !item.IsDeleted)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsDeleted, true)
                .SetProperty(item => item.Version, item => item.Version + 1)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
        if (updated == 0)
        {
            var exists = await db.StorageObjects.AsNoTracking().AnyAsync(item => item.Id == objectId, cancellationToken);
            await transaction.RollbackAsync(cancellationToken);
            return exists;
        }

        db.ChangeTracker.Clear();
        var currentGeneration = await db.StorageObjects.AsNoTracking()
            .Where(item => item.Id == objectId)
            .Select(item => item.CurrentGeneration)
            .SingleAsync(cancellationToken);
        var replicas = await db.StorageReplicas.AsNoTracking()
            .Where(item => item.StorageObjectGeneration.StorageObjectId == objectId
                           && item.Status != StorageReplicaStatuses.Deleted
                           && item.Status != StorageReplicaStatuses.DeletePending)
            .ToArrayAsync(cancellationToken);
        db.StorageTombstones.Add(new StorageTombstone
        {
            StorageObjectId = objectId,
            Generation = currentGeneration,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            LegalHold = policy.LegalHold,
            RetainUntil = policy.RetentionDays is { } retentionDays ? now.AddDays(retentionDays) : null,
            DeletedByUserId = deletedByUserId,
            DeletedAt = now,
        });
        var physicalDeleteAt = policy.RetentionDays is { } days ? now.AddDays(days) : now;
        foreach (var replica in replicas)
        {
            await db.StorageReplicas.Where(item => item.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, StorageReplicaStatuses.DeletePending)
                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (policy.LegalHold) continue;
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = objectId,
                StorageObjectGenerationId = replica.StorageObjectGenerationId,
                OperationType = StorageOperationTypes.Delete,
                Status = StorageOperationStatuses.Pending,
                IdempotencyKey = $"delete:{objectId:N}:{replica.Id}:{replica.ProviderKey}",
                ProviderKey = replica.ProviderKey,
                ConnectionId = replica.ConnectionId,
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(replica.Locator), JsonOptions),
                NextAttemptAt = physicalDeleteAt,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task MarkReplicaSuspectAsync(long replicaId, string error, CancellationToken cancellationToken)
    {
        var limitedError = error.Length <= 2000 ? error : error[..2000];
        var now = DateTime.UtcNow;
        await db.StorageReplicas.Where(item => item.Id == replicaId).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.Status, StorageReplicaStatuses.Suspect)
            .SetProperty(item => item.LastError, limitedError)
            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
    }

    private string BuildConnectedLocator(string providerKey, string relativeLocator)
    {
        var connectionId = options.CurrentValue.ProviderConnections.GetValueOrDefault(providerKey);
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new InvalidOperationException($"Storage provider '{providerKey}' has no configured connection id.");
        return $"{connectionId.Trim()}/{relativeLocator}";
    }

    private static string BuildOperationKey(string idempotencyKey, string suffix)
    {
        var value = $"write:{idempotencyKey}:{suffix}";
        return value.Length <= 160 ? value : $"write:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()}:{suffix}"[..160];
    }

    private static string SanitizeName(string name)
    {
        var safe = string.Concat(Path.GetFileName(name.Trim()).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return string.IsNullOrWhiteSpace(safe) ? "file.bin" : safe[..Math.Min(safe.Length, 180)];
    }

    private static string SanitizeSegment(string value)
    {
        var safe = new string(value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(safe) ? "data" : safe[..Math.Min(safe.Length, 64)];
    }

    private static void ValidateRequest(StorageObjectWriteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DataClass) || string.IsNullOrWhiteSpace(request.OwnerType)
            || string.IsNullOrWhiteSpace(request.OwnerId) || string.IsNullOrWhiteSpace(request.LogicalName)
            || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new ArgumentException("Data class, owner, logical name and idempotency key are required.");
        if (!request.Content.CanRead) throw new ArgumentException("Content stream must be readable.", nameof(request));
    }
}
