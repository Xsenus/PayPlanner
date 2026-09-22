using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record StorageOperationPayload(
    string TargetLocator,
    string? SourceProviderKey = null,
    string? SourceLocator = null);

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
    IStorageProviderHealthTracker? providerHealth = null) : IStorageOperationProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StorageOperationBatchResult> ProcessBatchAsync(string workerId, CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        var batchSize = Math.Clamp(settings.WorkerBatchSize, 1, 200);
        var leaseDuration = TimeSpan.FromSeconds(Math.Clamp(settings.WorkerLeaseSeconds, 15, 3600));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var candidates = await db.StorageOperations.AsNoTracking()
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
            .Select(item => item.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var claimed = new List<Guid>(candidates.Length);
        foreach (var operationId in candidates)
        {
            var updated = await db.StorageOperations
                .Where(item => item.Id == operationId && (
                    ((item.Status == StorageOperationStatuses.Pending
                      || item.Status == StorageOperationStatuses.Failed
                      || item.Status == StorageOperationStatuses.Unknown)
                     && (item.NextAttemptAt == null || item.NextAttemptAt <= now))
                    || (item.Status == StorageOperationStatuses.Running
                        && item.LeaseExpiresAt != null
                        && item.LeaseExpiresAt <= now)))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, StorageOperationStatuses.Running)
                    .SetProperty(item => item.LeaseOwner, workerId)
                    .SetProperty(item => item.LeaseExpiresAt, now.Add(leaseDuration))
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (updated == 1) claimed.Add(operationId);
        }

        var succeeded = 0;
        var retried = 0;
        var unknown = 0;
        var deadLettered = 0;
        foreach (var operationId in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = await ProcessClaimedAsync(operationId, workerId, settings, cancellationToken);
            switch (outcome)
            {
                case StorageOperationStatuses.Succeeded: succeeded++; break;
                case StorageOperationStatuses.Unknown: unknown++; break;
                case StorageOperationStatuses.DeadLetter: deadLettered++; break;
                default: retried++; break;
            }
        }

        return new StorageOperationBatchResult(claimed.Count, succeeded, retried, unknown, deadLettered);
    }

    private async Task<string> ProcessClaimedAsync(
        Guid operationId,
        string workerId,
        StorageOrchestrationOptions settings,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var operation = await db.StorageOperations
            .Include(item => item.StorageObject)
            .Include(item => item.StorageObjectGeneration)
                .ThenInclude(generation => generation!.Replicas)
            .SingleAsync(item => item.Id == operationId && item.LeaseOwner == workerId, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        operation.AttemptCount++;

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
            if (providerHealth is not null && !providerHealth.CanAttempt(operation.ProviderKey))
            {
                return await CompleteFailureAsync(operation, "Provider circuit is temporarily open.", settings, now, cancellationToken);
            }

            if (operation.OperationType is StorageOperationTypes.Replicate or StorageOperationTypes.Repair
                && operation.StorageObject.IsDeleted)
            {
                return await CompleteFailureAsync(operation, "Object is deleted; replication was suppressed by tombstone state.", settings, now, cancellationToken, permanent: true);
            }

            var result = operation.OperationType switch
            {
                StorageOperationTypes.Replicate or StorageOperationTypes.Repair =>
                    await ReplicateAsync(operation, payload!, target, cancellationToken),
                StorageOperationTypes.Verify =>
                    await VerifyAsync(operation, payload!, target, cancellationToken),
                StorageOperationTypes.Delete =>
                    await DeleteAsync(payload!, target, cancellationToken),
                _ => new OperationOutcome(StorageOperationStatus.Conflict, $"Unsupported operation type '{operation.OperationType}'."),
            };
            providerHealth?.Record(operation.ProviderKey, result.Status, result.Error);

            if (result.Status == StorageOperationStatus.Succeeded)
            {
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
                await db.SaveChangesAsync(cancellationToken);
                return operation.Status;
            }

            if (result.Status == StorageOperationStatus.Unknown)
            {
                operation.Status = StorageOperationStatuses.Unknown;
                operation.LastError = Limit(result.Error ?? "Provider outcome is unknown.");
                operation.NextAttemptAt = now.Add(ComputeRetryDelay(operation.Id, operation.AttemptCount));
                operation.LeaseOwner = null;
                operation.LeaseExpiresAt = null;
                operation.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return operation.Status;
            }

            return await CompleteFailureAsync(operation, result.Error ?? result.Status.ToString(), settings, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storage operation {OperationId} failed", operation.Id);
            return await CompleteFailureAsync(operation, ex.Message, settings, now, cancellationToken);
        }
    }

    private async Task<OperationOutcome> ReplicateAsync(
        StorageOperation operation,
        StorageOperationPayload payload,
        IStorageProvider target,
        CancellationToken cancellationToken)
    {
        var generation = operation.StorageObjectGeneration
            ?? throw new InvalidOperationException("Replication requires a storage generation.");
        if (string.IsNullOrWhiteSpace(payload.SourceProviderKey)
            || string.IsNullOrWhiteSpace(payload.SourceLocator)
            || !providers.TryGet(payload.SourceProviderKey, out var source))
        {
            return new OperationOutcome(StorageOperationStatus.Conflict, "Source provider or locator is unavailable.");
        }

        // UNKNOWN is reconciled before another write, preventing a blind duplicate on another destination.
        if (operation.Status == StorageOperationStatuses.Running && operation.AttemptCount > 0)
        {
            var existing = await target.HeadAsync(payload.TargetLocator, cancellationToken);
            if (Matches(existing, generation))
            {
                UpsertReplica(operation, payload.TargetLocator, existing, StorageReplicaStatuses.Available);
                return new OperationOutcome(StorageOperationStatus.Succeeded);
            }
        }

        await using var read = await source.OpenReadAsync(payload.SourceLocator, cancellationToken);
        if (read.Status != StorageOperationStatus.Succeeded || read.Content is null)
        {
            return new OperationOutcome(read.Status, read.Error ?? "Source object cannot be read.");
        }
        var write = await target.PutAsync(new StorageWriteRequest(
            payload.TargetLocator,
            read.Content,
            operation.StorageObject.ContentType,
            generation.SizeBytes,
            generation.Sha256,
            Overwrite: false), cancellationToken);
        if (write.Status == StorageOperationStatus.Conflict)
        {
            var existing = await target.HeadAsync(payload.TargetLocator, cancellationToken);
            if (Matches(existing, generation))
            {
                UpsertReplica(operation, payload.TargetLocator, existing, StorageReplicaStatuses.Available);
                return new OperationOutcome(StorageOperationStatus.Succeeded);
            }
        }
        if (write.Status == StorageOperationStatus.Succeeded)
        {
            var metadata = new StorageObjectMetadata(write.Status, write.Locator, write.SizeBytes, write.Sha256, null, write.ProviderVersion);
            UpsertReplica(operation, payload.TargetLocator, metadata, StorageReplicaStatuses.Available);
        }
        return new OperationOutcome(write.Status, write.Error);
    }

    private async Task<OperationOutcome> VerifyAsync(
        StorageOperation operation,
        StorageOperationPayload payload,
        IStorageProvider target,
        CancellationToken cancellationToken)
    {
        var generation = operation.StorageObjectGeneration
            ?? throw new InvalidOperationException("Verification requires a storage generation.");
        var metadata = await target.HeadAsync(payload.TargetLocator, cancellationToken);
        if (Matches(metadata, generation))
        {
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
        StorageOperationPayload payload,
        IStorageProvider target,
        CancellationToken cancellationToken)
    {
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
        await db.SaveChangesAsync(cancellationToken);
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

public sealed class StorageOperationWorker(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    ILogger<StorageOperationWorker> logger) : BackgroundService
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
                    var processor = scope.ServiceProvider.GetRequiredService<IStorageOperationProcessor>();
                    var result = await processor.ProcessBatchAsync(_workerId, stoppingToken);
                    if (result.Claimed > 0)
                    {
                        logger.LogInformation(
                            "Storage worker processed {Claimed} operations: {Succeeded} succeeded, {Retried} retry, {Unknown} unknown, {DeadLettered} dead-letter",
                            result.Claimed, result.Succeeded, result.Retried, result.Unknown, result.DeadLettered);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Storage operation worker iteration failed");
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.WorkerPollSeconds, 1, 300)), stoppingToken);
        }
    }
}
