using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageOperationPayload(
    string TargetLocator,
    string? SourceProviderKey = null,
    string? SourceLocator = null,
    string? OriginalTargetLocator = null);

public sealed record StorageOperationBatchResult(int Claimed, int Succeeded, int Retried, int Unknown, int DeadLettered);

public interface IStorageOperationProcessor
{
    Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken);
}

public sealed class StorageOperationProcessor(
    PaymentContext db,
    IStorageProviderRegistry providers,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    TimeProvider timeProvider,
    ILogger<StorageOperationProcessor> logger,
    IStorageProviderHealthTracker? providerHealth = null,
    ITaskFilePublicationProcessor? taskFilePublications = null,
    IDbContextFactory<PaymentContext>? contextFactory = null,
    ICaseDocumentPublicationProcessor? casePublications = null) : IStorageOperationProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var publishTaskFiles = taskFilePublications?.Enabled == true;
        var notifyTaskFiles = taskFilePublications?.NotificationsEnabled == true;
        if (publishTaskFiles) await taskFilePublications!.EnsureScheduledAsync(cancellationToken);
        var publishCaseDocuments = casePublications?.Enabled == true;
        if (publishCaseDocuments) await casePublications!.EnsureScheduledAsync(cancellationToken);
        var batchSize = Math.Clamp(settings.WorkerBatchSize, 1, 200);
        var leaseDuration = TimeSpan.FromSeconds(Math.Clamp(settings.WorkerLeaseSeconds, 15, 3600));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await db.StorageOperations.AsNoTracking()
            .Where(item => item.OperationType != StorageOperationTypes.PublishTaskFile || publishTaskFiles)
            .Where(item => item.OperationType != StorageOperationTypes.NotifyTaskFilePublication || notifyTaskFiles)
            .Where(item => item.OperationType != StorageOperationTypes.PublishCaseDocument || publishCaseDocuments)
            .Where(item =>
                ((item.Status == StorageOperationStatuses.Pending
                  || item.Status == StorageOperationStatuses.Failed
                  || item.Status == StorageOperationStatuses.Unknown)
                 && (item.NextAttemptAt == null || item.NextAttemptAt <= now))
                || (item.Status == StorageOperationStatuses.Running
                    && item.LeaseExpiresAt != null
                    && item.LeaseExpiresAt <= now))
            .OrderBy(item => item.NextAttemptAt ?? item.CreatedAt)
            .ThenBy(item => item.CreatedAt)
            .Select(item => new { item.Id, item.OperationType })
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var claimed = 0;
        var succeeded = 0;
        var retried = 0;
        var unknown = 0;
        var deadLettered = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationId = candidate.Id;
            now = timeProvider.GetUtcNow().UtcDateTime;
            // A fresh execution token fences a stale task even if the same worker ID is reused.
            var executionOwner = workerId[..Math.Min(workerId.Length, 90)] + ":" + Guid.NewGuid().ToString("N");
            var updated = await db.StorageOperations
                .Where(item => item.Id == operationId && item.OperationType == candidate.OperationType && (
                    ((item.Status == StorageOperationStatuses.Pending
                      || item.Status == StorageOperationStatuses.Failed
                      || item.Status == StorageOperationStatuses.Unknown)
                     && (item.NextAttemptAt == null || item.NextAttemptAt <= now))
                    || (item.Status == StorageOperationStatuses.Running
                        && item.LeaseExpiresAt != null
                        && item.LeaseExpiresAt <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, StorageOperationStatuses.Running)
                    .SetProperty(item => item.LeaseOwner, executionOwner)
                    .SetProperty(item => item.LeaseExpiresAt, now.Add(leaseDuration))
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (updated != 1) continue;
            claimed++;
            var outcome = await ProcessClaimedAsync(operationId, candidate.OperationType, executionOwner, settings, cancellationToken);
            switch (outcome)
            {
                case StorageOperationStatuses.Succeeded: succeeded++; break;
                case StorageOperationStatuses.Unknown: unknown++; break;
                case StorageOperationStatuses.DeadLetter: deadLettered++; break;
                default: retried++; break;
            }
        }

        return new StorageOperationBatchResult(claimed, succeeded, retried, unknown, deadLettered);
    }

    private async Task<string> ProcessClaimedAsync(
        Guid operationId,
        string operationType,
        string workerId,
        StorageOrchestrationOptions settings,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        if (operationType == StorageOperationTypes.PublishCaseDocument && casePublications is not null)
        {
            return await casePublications.ProcessClaimedAsync(operationId, workerId, cancellationToken);
        }
        // These metadata-only jobs resolve their own lease and exact intent;
        // do not load the object's entire replica graph just to dispatch them.
        if (operationType == StorageOperationTypes.PublishTaskFile && taskFilePublications is not null)
        {
            return await taskFilePublications.ProcessClaimedAsync(operationId, workerId, cancellationToken);
        }
        if (operationType == StorageOperationTypes.NotifyTaskFilePublication && taskFilePublications is not null)
        {
            return await taskFilePublications.ProcessNotificationClaimedAsync(operationId, workerId, cancellationToken);
        }
        var operation = await db.StorageOperations
            .Include(item => item.StorageObject)
            .Include(item => item.StorageObjectGeneration)
                .ThenInclude(generation => generation!.Replicas)
            .SingleOrDefaultAsync(item => item.Id == operationId && item.LeaseOwner == workerId
                && item.Status == StorageOperationStatuses.Running && item.LeaseExpiresAt > timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        if (operation is null) return StorageOperationStatuses.Running;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        operation.AttemptCount++;
        var operationSnapshot = db.Entry(operation).CurrentValues.Clone();
        var repairSnapshots = operation.StorageObjectGeneration is not null
            ? operation.StorageObjectGeneration.Replicas.Select(r => (Replica: r, Values: db.Entry(r).CurrentValues.Clone())).ToArray()
            : [];

        void RestoreRepairChanges()
        {
            if (operation.StorageObjectGeneration is not null)
            {
                var baseline = repairSnapshots.Select(s => s.Replica).ToHashSet();
                foreach (var replica in operation.StorageObjectGeneration.Replicas
                    .Where(r => !baseline.Contains(r)).ToArray())
                {
                    db.Entry(replica).State = EntityState.Detached;
                    operation.StorageObjectGeneration.Replicas.Remove(replica);
                }
            }
            foreach (var snapshot in repairSnapshots)
            {
                var entry = db.Entry(snapshot.Replica);
                entry.CurrentValues.SetValues(snapshot.Values);
                entry.OriginalValues.SetValues(snapshot.Values);
                entry.State = EntityState.Unchanged;
            }
            if (db.Entry(operation).State != EntityState.Detached)
            {
                db.Entry(operation).CurrentValues.SetValues(operationSnapshot);
                db.Entry(operation).OriginalValues.SetValues(operationSnapshot);
                db.Entry(operation).State = EntityState.Unchanged;
                db.Entry(operation).Property(o => o.AttemptCount).IsModified = true;
            }
        }

        void CaptureDurableRepairPlan()
        {
            operationSnapshot = db.Entry(operation).CurrentValues.Clone();
            repairSnapshots = operation.StorageObjectGeneration!.Replicas
                .Select(r => (Replica: r, Values: db.Entry(r).CurrentValues.Clone())).ToArray();
        }

        StorageOperationLeaseScope? renewal = null;
        try
        {
            if (!TryReadPayload(operation, out var payload, out var payloadError))
            {
                return await CompleteFailureAsync(operation, payloadError!, settings, now, cancellationToken, permanent: true);
            }
            if (string.IsNullOrWhiteSpace(operation.ProviderKey) || !providers.TryGet(operation.ProviderKey, out var target))
            {
                return await CompleteFailureAsync(operation, "Target provider is not registered.", settings, now, cancellationToken);
            }
            if (target is IStorageConnectedLocatorProvider connected
                && (!connected.TryGetConnectionId(payload!.TargetLocator, out var actualConnection) || actualConnection != operation.ConnectionId
                    || (payload.OriginalTargetLocator is not null
                        && (!connected.TryGetConnectionId(payload.OriginalTargetLocator, out var originalConnection) || originalConnection != operation.ConnectionId))))
                return await CompleteFailureAsync(operation, "Target locator does not match the registered storage connection.", settings, now, cancellationToken, permanent: true);
            if (providerHealth is not null && !providerHealth.CanAttempt(operation.ProviderKey))
            {
                return await CompleteFailureAsync(operation, "Provider circuit is temporarily open.", settings, now, cancellationToken);
            }

            if (operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Repair
                && operation.StorageObject.IsDeleted)
            {
                return await CompleteFailureAsync(operation, "Object is deleted; replication was suppressed by tombstone state.", settings, now, cancellationToken, permanent: true);
            }

            if (operation.OperationType == StorageOperationTypes.Delete)
            {
                var tombstone = await db.StorageTombstones.AsNoTracking().Where(t => t.StorageObjectId == operation.StorageObjectId)
                    .OrderByDescending(t => t.DeletedAt).FirstOrDefaultAsync(cancellationToken);
                if (!operation.StorageObject.IsDeleted || tombstone is null || operation.StorageObjectGeneration is null
                    || operation.StorageObjectGeneration.StorageObjectId != operation.StorageObjectId
                    || !operation.StorageObjectGeneration.Replicas.Any(r => r.ProviderKey == operation.ProviderKey
                        && r.ConnectionId == operation.ConnectionId && r.Locator == payload!.TargetLocator))
                    return await CompleteFailureAsync(operation, "Physical deletion has no matching tombstone and replica authority.", settings, now, cancellationToken, permanent: true);
                if (tombstone.LegalHold || tombstone.RetainUntil > now)
                {
                    await using var deferTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
                    if (await RepairLeaseQuery(operation).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, o => o.LeaseOwner), cancellationToken) != 1)
                    {
                        RestoreRepairChanges(); db.Entry(operation).State = EntityState.Detached;
                        return StorageOperationStatuses.Running;
                    }
                    operation.AttemptCount--;
                    operation.Status = StorageOperationStatuses.Pending;
                    operation.NextAttemptAt = tombstone.LegalHold ? now.AddHours(24) : tombstone.RetainUntil;
                    operation.LastError = "Physical deletion is deferred by retention policy.";
                    operation.LeaseOwner = null;
                    operation.LeaseExpiresAt = null;
                    operation.UpdatedAt = now;
                    await db.SaveChangesAsync(cancellationToken);
                    if (deferTransaction is not null) await deferTransaction.CommitAsync(cancellationToken);
                    return operation.Status;
                }
            }

            if (contextFactory is not null)
                renewal = new(contextFactory, operation, timeProvider, TimeSpan.FromSeconds(Math.Clamp(settings.WorkerLeaseSeconds, 15, 3600)), cancellationToken);
            var ioToken = renewal?.Token ?? cancellationToken;
            var result = operation.OperationType switch
            {
                StorageOperationTypes.Replicate or StorageOperationTypes.Repair =>
                    await ReplicateAsync(operation, payload!, target, CaptureDurableRepairPlan, ioToken),
                StorageOperationTypes.Verify =>
                    await VerifyAsync(operation, payload!, target, ioToken),
                StorageOperationTypes.Delete =>
                    await DeleteAsync(operation, payload!, target, ioToken),
                _ => new OperationOutcome(StorageOperationStatus.Conflict, $"Unsupported operation type '{operation.OperationType}'."),
            };
            if (renewal is not null) await renewal.StopAsync();
            now = timeProvider.GetUtcNow().UtcDateTime;
            if (operation.OperationType == StorageOperationTypes.Repair)
                payload = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!, JsonOptions)!;
            if (renewal?.LeaseLost == true || !await OwnsRepairLeaseAsync(operation, cancellationToken))
            {
                // Do not clear another worker's lease or save a stale terminal result.
                RestoreRepairChanges();
                db.Entry(operation).State = EntityState.Detached;
                return StorageOperationStatuses.Running;
            }
            providerHealth?.Record(operation.ProviderKey, result.Status, result.Error);

            if (result.Status == StorageOperationStatus.Succeeded)
            {
                if (RequiresVerifiedCopy(operation, payload!))
                {
                    if (await CompleteRepairSuccessAsync(operation, payload!, cancellationToken)) return StorageOperationStatuses.Succeeded;
                    RestoreRepairChanges();
                    db.Entry(operation).State = EntityState.Detached;
                    return StorageOperationStatuses.Running;
                }
                await using var completionTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
                if (await RepairLeaseQuery(operation).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, o => o.LeaseOwner), cancellationToken) != 1)
                {
                    RestoreRepairChanges(); db.Entry(operation).State = EntityState.Detached;
                    return StorageOperationStatuses.Running;
                }
                if (operation.OperationType == StorageOperationTypes.Delete && operation.StorageObjectGeneration is not null)
                {
                    var replica = operation.StorageObjectGeneration.Replicas.SingleOrDefault(item =>
                        string.Equals(item.ProviderKey, operation.ProviderKey, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(item.ConnectionId, operation.ConnectionId, StringComparison.Ordinal)
                        && string.Equals(item.Locator, payload!.TargetLocator, StringComparison.Ordinal));
                    if (replica is not null)
                    {
                        replica.Status = StorageReplicaStatuses.Deleted;
                        replica.LastError = null;
                        replica.UpdatedAt = now;
                    }
                }
                operation.Status = StorageOperationStatuses.Succeeded;
                operation.CompletedAt = now;
                operation.LastError = null;
                operation.NextAttemptAt = null;
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
                operation.UpdatedAt = now;
                if (operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Repair)
                    await StorageCapacityAdmission.AdvanceEpochAsync(db, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                if (completionTransaction is not null) await completionTransaction.CommitAsync(cancellationToken);
                return operation.Status;
            }

            if (result.Status == StorageOperationStatus.Unknown)
            {
                await using var unknownTransaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
                if (await RepairLeaseQuery(operation).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, o => o.LeaseOwner), cancellationToken) != 1)
                {
                    RestoreRepairChanges(); db.Entry(operation).State = EntityState.Detached;
                    return StorageOperationStatuses.Running;
                }
                operation.Status = StorageOperationStatuses.Unknown;
                operation.LastError = Limit(result.Error ?? "Provider outcome is unknown.");
                operation.NextAttemptAt = now.Add(ComputeRetryDelay(operation.Id, operation.AttemptCount));
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
                operation.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                if (unknownTransaction is not null) await unknownTransaction.CommitAsync(cancellationToken);
                return operation.Status;
            }

            var failureStatus = await CompleteFailureAsync(operation, result.Error ?? result.Status.ToString(), settings, now, cancellationToken);
            if (failureStatus == StorageOperationStatuses.Running) RestoreRepairChanges();
            return failureStatus;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && renewal?.LeaseLost == true)
        {
            RestoreRepairChanges();
            db.Entry(operation).State = EntityState.Detached;
            return StorageOperationStatuses.Running;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreRepairChanges();
            db.Entry(operation).State = EntityState.Detached;
            throw;
        }
        catch (Exception ex)
        {
            if (renewal is not null) await renewal.StopAsync();
            now = timeProvider.GetUtcNow().UtcDateTime;
            logger.LogWarning(ex, "Storage operation {OperationId} failed", operation.Id);
            RestoreRepairChanges();
            if (IsVerifiedCopy(operation))
            {
                if (await RepairWasCommittedAsync(operation, cancellationToken))
                {
                    // Commit may have succeeded before its acknowledgement was lost.
                    // Do not let a later unrelated SaveChanges revert the durable receipt.
                    foreach (var snapshot in repairSnapshots) db.Entry(snapshot.Replica).State = EntityState.Detached;
                    db.Entry(operation).State = EntityState.Detached;
                    return StorageOperationStatuses.Succeeded;
                }
            }
            var failureStatus = await CompleteFailureAsync(operation, ex.Message, settings, now, cancellationToken);
            if (failureStatus == StorageOperationStatuses.Running) RestoreRepairChanges();
            return failureStatus;
        }
        finally
        {
            if (renewal is not null) await renewal.DisposeAsync();
        }
    }

    private Task<bool> RepairWasCommittedAsync(StorageOperation operation, CancellationToken ct)
    {
        if (!TryReadPayload(operation, out var payload, out _) || operation.StorageObjectGeneration is null) return Task.FromResult(false);
        var generation = operation.StorageObjectGeneration;
        return db.StorageOperations.AsNoTracking().AnyAsync(o => o.Id == operation.Id && o.OperationType == operation.OperationType
            && o.Status == StorageOperationStatuses.Succeeded && o.CompletedAt != null && o.PayloadJson == operation.PayloadJson
            && o.StorageObjectId == operation.StorageObjectId && o.StorageObjectGenerationId == generation.Id
            && o.ProviderKey == operation.ProviderKey && o.ConnectionId == operation.ConnectionId
            && o.StorageObjectGeneration!.SizeBytes == generation.SizeBytes && o.StorageObjectGeneration.Sha256 == generation.Sha256
            && o.StorageObjectGeneration.Replicas.Any(r => r.ProviderKey == operation.ProviderKey && r.ConnectionId == operation.ConnectionId
                && r.Locator == payload!.TargetLocator && r.Status == StorageReplicaStatuses.Available
                && r.SizeBytes == generation.SizeBytes && r.Sha256 == generation.Sha256 && r.LastVerifiedAt != null)
            && (payload!.OriginalTargetLocator == null || o.StorageObjectGeneration.Replicas.Any(r =>
                r.ProviderKey == operation.ProviderKey && r.ConnectionId == operation.ConnectionId
                && r.Locator == payload.OriginalTargetLocator && r.Status == StorageReplicaStatuses.RetainedCorrupt)), ct);
    }

    private async Task<bool> CompleteRepairSuccessAsync(StorageOperation operation, StorageOperationPayload payload, CancellationToken ct)
    {
        // No remote I/O under this short transaction. No-op updates acquire row locks:
        // deletion, generation changes, replica rebinding and lease takeover must not
        // interleave between final authority verification and the catalog receipt.
        await using var transaction = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
        if (!await LockRepairAuthorityAsync(operation, payload, ct, requireSource: false)) return false;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        operation.Status = StorageOperationStatuses.Succeeded;
        operation.CompletedAt = now;
        operation.LastError = null;
        operation.NextAttemptAt = null;
        operation.LeaseOwner = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = now;
        if (operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Repair)
            await StorageCapacityAdmission.AdvanceEpochAsync(db, ct);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return true;
    }

    private async Task<bool> LockRepairAuthorityAsync(StorageOperation operation, StorageOperationPayload payload, CancellationToken ct, bool requireSource)
    {
        if (await db.StorageObjects.Where(o => o.Id == operation.StorageObjectId && !o.IsDeleted && !o.Tombstones.Any())
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Version, o => o.Version), ct) != 1) return false;
        var generation = operation.StorageObjectGeneration!;
        if (await db.StorageObjectGenerations.Where(g => g.Id == generation.Id && g.StorageObjectId == operation.StorageObjectId
            && g.Generation == generation.Generation && g.SizeBytes == generation.SizeBytes && g.Sha256 == generation.Sha256)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Generation, g => g.Generation), ct) != 1) return false;
        var key = operation.ProviderKey!.ToLowerInvariant();
        foreach (var locator in new[] { payload.TargetLocator, payload.OriginalTargetLocator }.Where(l => l is not null).OrderBy(l => l, StringComparer.Ordinal))
        {
            if (await db.StorageReplicas.Where(r => r.StorageObjectGenerationId == generation.Id && r.ProviderKey.ToLower() == key
                && r.ConnectionId == operation.ConnectionId && r.Locator == locator
                && r.Status != StorageReplicaStatuses.Deleted && r.Status != StorageReplicaStatuses.DeletePending)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status), ct) != 1) return false;
        }
        if (requireSource && await db.StorageReplicas.Where(r => r.StorageObjectGenerationId == generation.Id
            && r.ProviderKey.ToLower() == payload.SourceProviderKey!.ToLower() && r.Locator == payload.SourceLocator
            && r.Status == StorageReplicaStatuses.Available)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status), ct) != 1) return false;
        return await RepairLeaseQuery(operation).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, o => o.LeaseOwner), ct) == 1
            && await HasRepairAuthorityAsync(operation, payload, ct, requireSource);
    }

    private async Task<OperationOutcome> ReplicateAsync(
        StorageOperation operation,
        StorageOperationPayload payload,
        IStorageProvider target,
        Action captureDurablePlan,
        CancellationToken cancellationToken)
    {
        var generation = operation.StorageObjectGeneration
            ?? throw new InvalidOperationException("Replication requires a storage generation.");
        var repair = operation.OperationType == StorageOperationTypes.Repair;
        var verifiedCopy = RequiresVerifiedCopy(operation, payload);
        var legacyReplication = verifiedCopy && !repair;
        if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
            return new(StorageOperationStatus.Conflict, "Repair source, target or lease no longer matches the live catalog.");

        // UNKNOWN is reconciled before another write, preventing a blind duplicate on another destination.
        if (operation.Status == StorageOperationStatuses.Running && operation.AttemptCount > 0)
        {
            var existing = await target.HeadAsync(payload.TargetLocator, cancellationToken);
            if (Matches(existing, generation))
            {
                if (legacyReplication && !await VerifyTargetBytesAsync(target, payload.TargetLocator, generation, cancellationToken))
                    return new(StorageOperationStatus.Conflict, "Legacy migration target bytes cannot be verified.");
                if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
                    return new(StorageOperationStatus.Conflict, "Repair authority changed during target verification.");
                PublishReplica(operation, payload, existing);
                return new OperationOutcome(StorageOperationStatus.Succeeded);
            }
            if (repair && existing.Status != StorageOperationStatus.Succeeded && existing.Status != StorageOperationStatus.NotFound)
                return new(existing.Status, "Repair target cannot be verified; writing was suppressed.");
        }

        if (string.IsNullOrWhiteSpace(payload.SourceProviderKey) || string.IsNullOrWhiteSpace(payload.SourceLocator)
            || !providers.TryGet(payload.SourceProviderKey, out var source))
            return new(StorageOperationStatus.Conflict, "Source provider or locator is unavailable.");
        if (!repair && source is IStorageConnectedLocatorProvider sourceConnection
            && (!sourceConnection.TryGetConnectionId(payload.SourceLocator, out var actualSourceConnection)
                || !await db.StorageReplicas.AsNoTracking().AnyAsync(r => r.StorageObjectGenerationId == generation.Id
                    && r.ProviderKey.ToLower() == payload.SourceProviderKey.ToLower() && r.Locator == payload.SourceLocator
                    && r.ConnectionId == actualSourceConnection && r.Status == StorageReplicaStatuses.Available, cancellationToken)))
            return new(StorageOperationStatus.Conflict, "Source locator does not match the registered storage connection.");
        if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken))
            return new(StorageOperationStatus.Conflict, "Repair source no longer matches the live catalog.");

        await using var read = await source.OpenReadAsync(payload.SourceLocator, cancellationToken);
        if (read.Status != StorageOperationStatus.Succeeded || read.Content is null)
        {
            return new OperationOutcome(read.Status, read.Error ?? "Source object cannot be read.");
        }
        await using var verified = verifiedCopy ? await VerifyRepairSourceAsync(read, generation, operation.Id, cancellationToken) : null;
        if (verifiedCopy)
        {
            if (verified is null) return new(StorageOperationStatus.Conflict, "Repair source failed size/checksum verification.");
            if (!await HasRepairAuthorityAsync(operation, payload, cancellationToken))
                return new(StorageOperationStatus.Conflict, "Repair authority changed while reading source bytes.");
            var current = await target.HeadAsync(payload.TargetLocator, cancellationToken);
            if (Matches(current, generation))
            {
                if (legacyReplication && !await VerifyTargetBytesAsync(target, payload.TargetLocator, generation, cancellationToken))
                    return new(StorageOperationStatus.Conflict, "Legacy migration target bytes cannot be verified.");
                if (!await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
                    return new(StorageOperationStatus.Conflict, "Repair authority changed during target verification.");
                PublishReplica(operation, payload, current);
                return new(StorageOperationStatus.Succeeded);
            }
            if (current.Status != StorageOperationStatus.Succeeded && current.Status != StorageOperationStatus.NotFound)
                return new(current.Status, "Repair target became unverifiable; writing was suppressed.");
            if (repair && payload.OriginalTargetLocator is null && current.Status == StorageOperationStatus.Succeeded
                && (current.SizeBytes != generation.SizeBytes || (!string.IsNullOrWhiteSpace(current.Sha256)
                    && !string.Equals(current.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase))))
            {
                var planned = await UpgradeLegacyRepairAsync(operation, payload, target, captureDurablePlan, cancellationToken);
                if (planned is null) return new(StorageOperationStatus.Conflict, "Legacy repair cannot acquire a durable isolated destination.");
                payload = planned;
                if (!await HasRepairAuthorityAsync(operation, payload, cancellationToken))
                    return new(StorageOperationStatus.Conflict, "Repair authority changed after the durable destination plan.");
                // A pre-existing destination is checked by the ordinary conflict/receipt path; never overwrite it.
            }
        }
        var write = await target.PutAsync(new StorageWriteRequest(
            payload.TargetLocator,
            verified ?? read.Content,
            operation.StorageObject.ContentType,
            generation.SizeBytes,
            generation.Sha256,
            Overwrite: false), cancellationToken);
        if (write.Status == StorageOperationStatus.Conflict)
        {
            var existing = await target.HeadAsync(payload.TargetLocator, cancellationToken);
            if (Matches(existing, generation))
            {
                if (legacyReplication && !await VerifyTargetBytesAsync(target, payload.TargetLocator, generation, cancellationToken))
                    return new(StorageOperationStatus.Conflict, "Legacy migration target bytes cannot be verified.");
                if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
                    return new(StorageOperationStatus.Conflict, "Repair authority changed during conflict verification.");
                PublishReplica(operation, payload, existing);
                return new OperationOutcome(StorageOperationStatus.Succeeded);
            }
        }
        if (write.Status == StorageOperationStatus.Succeeded)
        {
            var metadata = new StorageObjectMetadata(write.Status, write.Locator, write.SizeBytes, write.Sha256, null, write.ProviderVersion);
            if (verifiedCopy && (!Matches(metadata, generation) || !string.Equals(write.Locator, payload.TargetLocator, StringComparison.Ordinal)))
                return new(StorageOperationStatus.Conflict, "Repair acknowledgement does not match the expected target and generation.");
            if (legacyReplication && !await VerifyTargetBytesAsync(target, payload.TargetLocator, generation, cancellationToken))
                return new(StorageOperationStatus.Conflict, "Legacy migration target bytes cannot be verified.");
            if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
                return new(StorageOperationStatus.Conflict, "Repair authority changed during target write.");
            PublishReplica(operation, payload, metadata);
        }
        return new OperationOutcome(write.Status, write.Error);
    }

    private async Task<StorageOperationPayload?> UpgradeLegacyRepairAsync(StorageOperation operation, StorageOperationPayload payload,
        IStorageProvider target, Action captureDurablePlan, CancellationToken ct)
    {
        if (target is not IStorageRepairDestinationProvider destinations || db.Database.CurrentTransaction is not null) return null;
        var generation = operation.StorageObjectGeneration!;
        var locator = destinations.CreateRepairLocator(payload.TargetLocator, generation.Id, operation.IdempotencyKey);
        if (locator == payload.TargetLocator || string.IsNullOrWhiteSpace(locator)) return null;
        if (target is IStorageConnectedLocatorProvider connected
            && (!connected.TryGetConnectionId(locator, out var connection) || connection != operation.ConnectionId)) return null;
        var planned = payload with { TargetLocator = locator, OriginalTargetLocator = payload.TargetLocator };
        var plannedJson = JsonSerializer.Serialize(planned, JsonOptions);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await LockRepairAuthorityAsync(operation, payload, ct, requireSource: true)) return null;
        if (await db.StorageReplicas.AsNoTracking().AnyAsync(r => r.StorageObjectGenerationId == generation.Id
            && r.ProviderKey == operation.ProviderKey && r.ConnectionId == operation.ConnectionId && r.Locator == locator, ct)) return null;
        var original = generation.Replicas.Single(r => string.Equals(r.ProviderKey, operation.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && r.ConnectionId == operation.ConnectionId && r.Locator == payload.TargetLocator);
        original.Status = StorageReplicaStatuses.Corrupt;
        original.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        var replacement = new StorageReplica
        {
            StorageObjectGenerationId = generation.Id,
            ProviderKey = operation.ProviderKey!,
            ConnectionId = operation.ConnectionId,
            Locator = locator,
            Status = StorageReplicaStatuses.RepairPending,
            SizeBytes = generation.SizeBytes,
            Sha256 = generation.Sha256
        };
        generation.Replicas.Add(replacement);
        db.StorageReplicas.Add(replacement);
        operation.PayloadJson = plannedJson;
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            captureDurablePlan();
            return planned;
        }
        catch
        {
            // A lost commit acknowledgement may leave a durable plan. Dispose the
            // transaction before checking it; no provider write occurred yet.
            await transaction.DisposeAsync();
            if (await db.StorageOperations.AsNoTracking().AnyAsync(o => o.Id == operation.Id && o.PayloadJson == plannedJson
                && o.StorageObjectGeneration!.Replicas.Any(r => r.Id == replacement.Id && r.Locator == locator
                    && r.Status == StorageReplicaStatuses.RepairPending), CancellationToken.None)) captureDurablePlan();
            throw;
        }
    }

    private IQueryable<StorageOperation> RepairLeaseQuery(StorageOperation operation)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return db.StorageOperations.Where(item => item.Id == operation.Id
            && item.Status == StorageOperationStatuses.Running && item.LeaseOwner == operation.LeaseOwner
            && item.LeaseExpiresAt > now && item.OperationType == operation.OperationType
            && item.StorageObjectId == operation.StorageObjectId && item.StorageObjectGenerationId == operation.StorageObjectGenerationId
            && item.ProviderKey == operation.ProviderKey && item.ConnectionId == operation.ConnectionId && item.PayloadJson == operation.PayloadJson);
    }

    private Task<bool> OwnsRepairLeaseAsync(StorageOperation operation, CancellationToken ct) => RepairLeaseQuery(operation).AnyAsync(ct);

    private Task<bool> HasRepairAuthorityAsync(StorageOperation operation, StorageOperationPayload payload, CancellationToken ct, bool requireSource = true)
    {
        if (!providers.TryGet(operation.ProviderKey!, out var target)) return Task.FromResult(false);
        if (target is IStorageConnectedLocatorProvider connected
            && (!connected.TryGetConnectionId(payload.TargetLocator, out var connection) || connection != operation.ConnectionId
                || (payload.OriginalTargetLocator is not null && (!connected.TryGetConnectionId(payload.OriginalTargetLocator, out var originalConnection)
                    || originalConnection != operation.ConnectionId)))) return Task.FromResult(false);
        string? sourceConnectionId = null;
        if (requireSource && payload.SourceProviderKey is not null && providers.TryGet(payload.SourceProviderKey, out var source)
            && source is IStorageConnectedLocatorProvider sourceConnection)
        {
            if (payload.SourceLocator is null || !sourceConnection.TryGetConnectionId(payload.SourceLocator, out var parsedSourceConnection)) return Task.FromResult(false);
            sourceConnectionId = parsedSourceConnection;
        }
        if (payload.OriginalTargetLocator is not null && (!providers.TryGet(operation.ProviderKey!, out var provider)
            || provider is not IStorageRepairDestinationProvider destinations
            || payload.TargetLocator != destinations.CreateRepairLocator(payload.OriginalTargetLocator,
                operation.StorageObjectGenerationId!.Value, operation.IdempotencyKey))) return Task.FromResult(false);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var sourceKey = payload.SourceProviderKey?.ToLowerInvariant();
        var targetKey = operation.ProviderKey!.ToLowerInvariant();
        var checksum = operation.StorageObjectGeneration!.Sha256.ToLowerInvariant();
        var expectedSize = operation.StorageObjectGeneration.SizeBytes;
        var expectedGeneration = operation.StorageObjectGeneration.Generation;
        var legacyReplication = operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Verify;
        return db.StorageOperations.AsNoTracking().AnyAsync(item => item.Id == operation.Id
            && item.Status == StorageOperationStatuses.Running && item.LeaseOwner == operation.LeaseOwner && item.LeaseExpiresAt > now
            && item.OperationType == operation.OperationType && item.ProviderKey == operation.ProviderKey
            && item.ConnectionId == operation.ConnectionId && item.PayloadJson == operation.PayloadJson
            && item.StorageObjectId == operation.StorageObjectId && item.StorageObjectGenerationId == operation.StorageObjectGenerationId
            && !item.StorageObject.IsDeleted && !item.StorageObject.Tombstones.Any()
            && (!legacyReplication || (item.StorageObject.CurrentGeneration == expectedGeneration
                && item.StorageObject.Version == operation.StorageObject.Version
                && item.StorageObject.DataClass == operation.StorageObject.DataClass
                && item.StorageObject.OwnerType == operation.StorageObject.OwnerType && item.StorageObject.OwnerId == operation.StorageObject.OwnerId))
            && item.StorageObjectGeneration != null && item.StorageObjectGeneration.StorageObjectId == item.StorageObjectId
            && item.StorageObjectGeneration.Generation == expectedGeneration && item.StorageObjectGeneration.SizeBytes == expectedSize
            && item.StorageObjectGeneration.Sha256.ToLower() == checksum
            && (!requireSource || item.StorageObjectGeneration.Replicas.Any(r => r.ProviderKey.ToLower() == sourceKey
                && r.Locator == payload.SourceLocator && r.Status == StorageReplicaStatuses.Available
                && r.ConnectionId == sourceConnectionId
                && r.SizeBytes == item.StorageObjectGeneration.SizeBytes && r.Sha256 != null && r.Sha256.ToLower() == checksum))
            && (payload.OriginalTargetLocator == null || item.StorageObjectGeneration.Replicas.Any(r => r.ProviderKey.ToLower() == targetKey
                && r.ConnectionId == operation.ConnectionId && r.Locator == payload.OriginalTargetLocator
                && (r.Status == StorageReplicaStatuses.Corrupt || r.Status == StorageReplicaStatuses.RetainedCorrupt)))
            && item.StorageObjectGeneration.Replicas.Any(r => r.ProviderKey.ToLower() == targetKey
                && r.ConnectionId == operation.ConnectionId && r.Locator == payload.TargetLocator
                && r.Status != StorageReplicaStatuses.Deleted && r.Status != StorageReplicaStatuses.DeletePending), ct);
    }

    private void PublishReplica(StorageOperation operation, StorageOperationPayload payload, StorageObjectMetadata metadata)
    {
        UpsertReplica(operation, payload.TargetLocator, metadata, StorageReplicaStatuses.Available);
        if (operation.OperationType != StorageOperationTypes.Repair || payload.OriginalTargetLocator is null) return;
        var original = operation.StorageObjectGeneration!.Replicas.Single(r =>
            string.Equals(r.ProviderKey, operation.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && r.ConnectionId == operation.ConnectionId && r.Locator == payload.OriginalTargetLocator);
        original.Status = StorageReplicaStatuses.RetainedCorrupt;
        original.LastError = "Damaged bytes retained; verified replacement: " + payload.TargetLocator;
        original.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private static bool RequiresVerifiedCopy(StorageOperation operation, StorageOperationPayload payload)
        => operation.OperationType == StorageOperationTypes.Repair
            || (operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Verify
                && string.Equals(payload.SourceProviderKey, "legacy-local", StringComparison.OrdinalIgnoreCase));

    private static bool IsVerifiedCopy(StorageOperation operation)
        => TryReadPayload(operation, out var payload, out _) && RequiresVerifiedCopy(operation, payload!);

    private static async Task<bool> VerifyTargetBytesAsync(IStorageProvider target, string locator,
        StorageObjectGeneration generation, CancellationToken ct)
    {
        await using var read = await target.OpenReadAsync(locator, ct);
        if (read.Status != StorageOperationStatus.Succeeded || read.Content is null) return false;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int count;
        while ((count = await read.Content.ReadAsync(buffer, ct)) > 0)
        {
            if (count > generation.SizeBytes - size) return false;
            size += count;
            hash.AppendData(buffer.AsSpan(0, count));
        }
        return size == generation.SizeBytes
            && string.Equals(Convert.ToHexString(hash.GetHashAndReset()), generation.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<FileStream?> VerifyRepairSourceAsync(StorageReadResult read, StorageObjectGeneration generation, Guid operationId, CancellationToken ct)
    {
        if (generation.SizeBytes < 0 || read.SizeBytes != generation.SizeBytes || generation.Sha256.Length != 64
            || !generation.Sha256.All(Uri.IsHexDigit)) return null;
        // A bounded-memory spool proves all bytes BEFORE any repair PUT. DeleteOnClose also
        // removes partial input on cancellation, oversized streams or checksum failure.
        var path = Path.Combine(Path.GetTempPath(), $"pp_verified_repair_{operationId:N}_{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 81920,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var spool = new FileStream(path, options);
        var verified = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            int count;
            while ((count = await read.Content!.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (count > generation.SizeBytes - size) return null;
                hash.AppendData(buffer.AsSpan(0, count));
                await spool.WriteAsync(buffer.AsMemory(0, count), ct);
                size += count;
            }
            if (size != generation.SizeBytes || !string.Equals(Convert.ToHexString(hash.GetHashAndReset()), generation.Sha256, StringComparison.OrdinalIgnoreCase))
                return null;
            await spool.FlushAsync(ct);
            spool.Position = 0;
            verified = true;
            return spool;
        }
        finally { if (!verified) await spool.DisposeAsync(); }
    }

    private async Task<OperationOutcome> VerifyAsync(
        StorageOperation operation,
        StorageOperationPayload payload,
        IStorageProvider target,
        CancellationToken cancellationToken)
    {
        var generation = operation.StorageObjectGeneration
            ?? throw new InvalidOperationException("Verification requires a storage generation.");
        var verifiedCopy = RequiresVerifiedCopy(operation, payload);
        if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
            return new(StorageOperationStatus.Conflict, "Legacy verification catalogue authority changed.");
        var metadata = await target.HeadAsync(payload.TargetLocator, cancellationToken);
        if (verifiedCopy && !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false))
            return new(StorageOperationStatus.Conflict, "Legacy verification catalogue authority changed during provider read.");
        if (Matches(metadata, generation))
        {
            if (verifiedCopy && (!await VerifyTargetBytesAsync(target, payload.TargetLocator, generation, cancellationToken)
                || !await HasRepairAuthorityAsync(operation, payload, cancellationToken, requireSource: false)))
                return new(StorageOperationStatus.Conflict, "Legacy verification target bytes or catalogue authority changed.");
            UpsertReplica(operation, payload.TargetLocator, metadata, StorageReplicaStatuses.Available);
            return new OperationOutcome(StorageOperationStatus.Succeeded);
        }
        if (metadata.Status == StorageOperationStatus.Succeeded)
        {
            UpsertReplica(operation, payload.TargetLocator, metadata, StorageReplicaStatuses.Corrupt);
            return new OperationOutcome(StorageOperationStatus.Conflict, "Replica checksum or size does not match the generation.");
        }
        return new OperationOutcome(metadata.Status, metadata.Error ?? "Replica is unavailable.");
    }

    private static async Task<OperationOutcome> DeleteAsync(
        StorageOperation operation,
        StorageOperationPayload payload,
        IStorageProvider target,
        CancellationToken cancellationToken)
    {
        var metadata = await target.HeadAsync(payload.TargetLocator, cancellationToken);
        if (metadata.Status == StorageOperationStatus.NotFound)
            return new OperationOutcome(StorageOperationStatus.Succeeded);
        if (metadata.Status != StorageOperationStatus.Succeeded)
            return new OperationOutcome(metadata.Status, metadata.Error ?? "Deletion target could not be verified.");
        var generation = operation.StorageObjectGeneration!;
        if (metadata.SizeBytes != generation.SizeBytes
            || !string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase))
            return new OperationOutcome(StorageOperationStatus.Conflict, "Deletion target no longer matches the registered generation.");
        var result = await target.DeleteAsync(payload.TargetLocator, cancellationToken);
        return result.Status == StorageOperationStatus.NotFound
            ? new OperationOutcome(StorageOperationStatus.Succeeded)
            : new OperationOutcome(result.Status, result.Error);
    }

    private void UpsertReplica(StorageOperation operation, string locator, StorageObjectMetadata metadata, string status)
    {
        var generation = operation.StorageObjectGeneration!;
        var replica = generation.Replicas.SingleOrDefault(item =>
            string.Equals(item.ProviderKey, operation.ProviderKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ConnectionId, operation.ConnectionId, StringComparison.Ordinal)
            && string.Equals(item.Locator, locator, StringComparison.Ordinal));
        if (replica is null)
        {
            replica = new StorageReplica
            {
                ProviderKey = operation.ProviderKey!,
                ConnectionId = operation.ConnectionId,
                Locator = locator,
                CreatedAt = timeProvider.GetUtcNow().UtcDateTime,
            };
            generation.Replicas.Add(replica);
        }
        replica.Status = status;
        replica.SizeBytes = metadata.SizeBytes;
        replica.Sha256 = metadata.Sha256;
        replica.ProviderVersion = metadata.ProviderVersion;
        replica.LastError = metadata.Error;
        replica.LastVerifiedAt = timeProvider.GetUtcNow().UtcDateTime;
        replica.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
    }

    private async Task<string> CompleteFailureAsync(
        StorageOperation operation,
        string error,
        StorageOrchestrationOptions settings,
        DateTime now,
        CancellationToken cancellationToken,
        bool permanent = false)
    {
        await using var repairTransaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (await RepairLeaseQuery(operation).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseOwner, o => o.LeaseOwner), cancellationToken) != 1)
        {
            db.Entry(operation).State = EntityState.Detached;
            return StorageOperationStatuses.Running;
        }
        var maxAttempts = Math.Clamp(settings.WorkerMaxAttempts, 1, 100);
        operation.Status = permanent || operation.AttemptCount >= maxAttempts
            ? StorageOperationStatuses.DeadLetter
            : StorageOperationStatuses.Failed;
        operation.LastError = Limit(error);
        operation.NextAttemptAt = operation.Status == StorageOperationStatuses.DeadLetter
            ? null
            : now.Add(ComputeRetryDelay(operation.Id, operation.AttemptCount));
        operation.CompletedAt = operation.Status == StorageOperationStatuses.DeadLetter ? now : null;
        operation.LeaseOwner = null;
        operation.LeaseExpiresAt = null;
        operation.UpdatedAt = now;
        if (operation.Status == StorageOperationStatuses.DeadLetter
            && operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Repair)
            await StorageCapacityAdmission.AdvanceEpochAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (repairTransaction is not null) await repairTransaction.CommitAsync(cancellationToken);
        return operation.Status;
    }

    private static bool TryReadPayload(StorageOperation operation, out StorageOperationPayload? payload, out string? error)
    {
        try
        {
            payload = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson ?? string.Empty, JsonOptions);
            error = payload is null || string.IsNullOrWhiteSpace(payload.TargetLocator) ? "Operation payload is incomplete." : null;
            return error is null;
        }
        catch (JsonException ex)
        {
            payload = null;
            error = $"Operation payload is invalid: {ex.Message}";
            return false;
        }
    }

    private static bool Matches(StorageObjectMetadata metadata, StorageObjectGeneration generation)
        => metadata.Status == StorageOperationStatus.Succeeded
           && metadata.SizeBytes == generation.SizeBytes
           && string.Equals(metadata.Sha256, generation.Sha256, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan ComputeRetryDelay(Guid operationId, int attempt)
    {
        var exponentialSeconds = Math.Min(900, Math.Pow(2, Math.Clamp(attempt, 1, 10)));
        var jitter = operationId.ToByteArray()[0] % 17;
        return TimeSpan.FromSeconds(exponentialSeconds + jitter);
    }

    private static string Limit(string value) => value.Length <= 2000 ? value : value[..2000];

    private sealed record OperationOutcome(StorageOperationStatus Status, string? Error = null);
}

/// <summary>Immutable startup registration snapshot; a configuration reload cannot start a missing worker.</summary>
public sealed record StorageWorkerAvailability(bool IsRegistered)
{
    public static StorageWorkerAvailability Capture(IServiceCollection services) => new(services.Any(service =>
        service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(StorageOperationWorker)));
}

public sealed class StorageOperationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    ILogger<StorageOperationWorker> logger,
    StorageStageMarkerMaintenance markerMaintenance) : BackgroundService
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.CurrentValue;
            if (settings.WorkerEnabled)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var recovery = scope.ServiceProvider.GetService<GeneratedFinancialAttemptRecovery>();
                    if (recovery is not null) await recovery.ProcessBatchAsync(stoppingToken);
                    var processor = scope.ServiceProvider.GetRequiredService<IStorageOperationProcessor>();
                    var result = await processor.ProcessBatchAsync(_workerId, stoppingToken);
                    if (result.Claimed > 0)
                    {
                        logger.LogInformation(
                            "Storage worker processed {Claimed} operations: {Succeeded} succeeded, {Retried} retry, {Unknown} unknown, {DeadLettered} dead-letter",
                            result.Claimed, result.Succeeded, result.Retried, result.Unknown, result.DeadLettered);
                    }
                    // Chat publication runs after copy processing, in its own clean
                    // unit of work; copy/financial tracker state must not leak into it.
                    var chatSettings = options.CurrentValue;
                    if (chatSettings.Enabled && chatSettings.WorkerEnabled && chatSettings.UploadCapacityChecksEnabled
                        && chatSettings.ProductWriteEnabledClasses.Contains("chat-attachment", StringComparer.Ordinal))
                    {
                        using var chatScope = scopeFactory.CreateScope();
                        var chatRecovery = chatScope.ServiceProvider.GetService<InternalChatSendRecovery>();
                        if (chatRecovery is not null) await chatRecovery.ProcessBatchAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Storage operation worker iteration failed");
                }
                // Cleanup has its own scope and failure boundary. A copy failure
                // cannot starve it, and a cleanup failure cannot stop copy work.
                try
                {
                    var markers = await markerMaintenance.ProcessIfDueAsync(stoppingToken);
                    if (markers is { Removed: > 0 })
                        logger.LogInformation("Storage worker removed {Removed} released stage markers from {Inspected} reservations",
                            markers.Removed, markers.Inspected);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Storage stage marker maintenance failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.WorkerPollSeconds, 1, 300)), stoppingToken);
        }
    }
}
