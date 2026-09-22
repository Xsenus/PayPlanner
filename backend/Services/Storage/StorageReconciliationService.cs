using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageReconciliationIssue(
    Guid ObjectId,
    Guid GenerationId,
    long ReplicaId,
    string ProviderKey,
    string Locator,
    string Kind,
    string? Detail);

public sealed record StorageReconciliationReport(
    bool Applied,
    int ObjectsChecked,
    int ReplicasChecked,
    int HealthyReplicas,
    int MissingReplicas,
    int CorruptReplicas,
    int DeleteOperationsQueued,
    int RepairOperationsQueued,
    IReadOnlyList<StorageReconciliationIssue> Issues);

public interface IStorageReconciliationService
{
    Task<StorageReconciliationReport> RunAsync(bool apply, CancellationToken cancellationToken);
}

public sealed class StorageReconciliationService(
    PaymentContext db,
    IStorageProviderRegistry providers) : IStorageReconciliationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StorageReconciliationReport> RunAsync(bool apply, CancellationToken cancellationToken)
    {
        var objects = await db.StorageObjects.AsNoTracking()
            .Include(item => item.Generations).ThenInclude(item => item.Replicas)
            .Include(item => item.Tombstones)
            .ToListAsync(cancellationToken);
        var issues = new List<StorageReconciliationIssue>();
        var checkedReplicas = 0;
        var healthy = 0;
        var missing = 0;
        var corrupt = 0;
        var deletes = 0;
        var repairs = 0;
        var now = DateTime.UtcNow;

        foreach (var storageObject in objects)
        {
            foreach (var generation in storageObject.Generations)
            {
                var healthySources = new List<StorageReplica>();
                foreach (var replica in generation.Replicas.Where(item => item.Status != StorageReplicaStatuses.Deleted))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkedReplicas++;
                    if (storageObject.IsDeleted)
                    {
                        var tombstone = storageObject.Tombstones.OrderByDescending(item => item.Generation).FirstOrDefault();
                        var deletionDeferred = tombstone?.LegalHold == true || tombstone?.RetainUntil > now;
                        issues.Add(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                            deletionDeferred ? "retained" : "deletePending",
                            tombstone?.LegalHold == true
                                ? "Physical deletion is blocked by legal hold."
                                : tombstone?.RetainUntil > now
                                    ? $"Physical deletion is retained until {tombstone.RetainUntil:O}."
                                    : "Logical tombstone exists but physical replica remains."));
                        if (apply && !deletionDeferred
                            && await EnqueueDeleteAsync(storageObject.Id, generation.Id, replica, now, cancellationToken)) deletes++;
                        continue;
                    }
                    if (!providers.TryGet(replica.ProviderKey, out var provider))
                    {
                        issues.Add(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                            "providerUnavailable", "Provider is not registered."));
                        continue;
                    }
                    var metadata = await provider.HeadAsync(replica.Locator, cancellationToken);
                    if (metadata.Status == StorageOperationStatus.Succeeded
                        && metadata.SizeBytes == generation.SizeBytes
                        && string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        healthy++;
                        healthySources.Add(replica);
                        if (apply)
                        {
                            await db.StorageReplicas.Where(item => item.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                                .SetProperty(item => item.Status, StorageReplicaStatuses.Available)
                                .SetProperty(item => item.LastVerifiedAt, now)
                                .SetProperty(item => item.LastError, (string?)null)
                                .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                        }
                        continue;
                    }

                    var kind = metadata.Status == StorageOperationStatus.NotFound ? "missing" : "corrupt";
                    if (kind == "missing") missing++; else corrupt++;
                    issues.Add(new(storageObject.Id, generation.Id, replica.Id, replica.ProviderKey, replica.Locator,
                        kind, metadata.Error));
                    if (apply)
                    {
                        var status = kind == "missing" ? StorageReplicaStatuses.Missing : StorageReplicaStatuses.Corrupt;
                        await db.StorageReplicas.Where(item => item.Id == replica.Id).ExecuteUpdateAsync(setters => setters
                            .SetProperty(item => item.Status, status)
                            .SetProperty(item => item.LastError, metadata.Error ?? $"Replica is {kind}.")
                            .SetProperty(item => item.UpdatedAt, now), cancellationToken);
                    }
                }

                if (apply && healthySources.Count > 0)
                {
                    var source = healthySources[0];
                    foreach (var issue in issues.Where(item => item.GenerationId == generation.Id && item.Kind is "missing" or "corrupt"))
                    {
                        var target = generation.Replicas.Single(item => item.Id == issue.ReplicaId);
                        if (await EnqueueRepairAsync(storageObject.Id, generation.Id, source, target, now, cancellationToken)) repairs++;
                    }
                }
            }
        }

        if (apply) await db.SaveChangesAsync(cancellationToken);
        return new(apply, objects.Count, checkedReplicas, healthy, missing, corrupt, deletes, repairs, issues);
    }

    private async Task<bool> EnqueueDeleteAsync(
        Guid objectId,
        Guid generationId,
        StorageReplica replica,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var key = $"reconcile:delete:{generationId:N}:{replica.Id}";
        if (await db.StorageOperations.AnyAsync(item => item.IdempotencyKey == key, cancellationToken)) return false;
        db.StorageOperations.Add(new StorageOperation
        {
            StorageObjectId = objectId,
            StorageObjectGenerationId = generationId,
            OperationType = StorageOperationTypes.Delete,
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = key,
            ProviderKey = replica.ProviderKey,
            ConnectionId = replica.ConnectionId,
            PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(replica.Locator), JsonOptions),
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        return true;
    }

    private async Task<bool> EnqueueRepairAsync(
        Guid objectId,
        Guid generationId,
        StorageReplica source,
        StorageReplica target,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var key = $"reconcile:repair:{generationId:N}:{target.Id}";
        if (await db.StorageOperations.AnyAsync(item => item.IdempotencyKey == key, cancellationToken)) return false;
        db.StorageOperations.Add(new StorageOperation
        {
            StorageObjectId = objectId,
            StorageObjectGenerationId = generationId,
            OperationType = StorageOperationTypes.Repair,
            Status = StorageOperationStatuses.Pending,
            IdempotencyKey = key,
            ProviderKey = target.ProviderKey,
            ConnectionId = target.ConnectionId,
            PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(target.Locator, source.ProviderKey, source.Locator), JsonOptions),
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        return true;
    }
}
