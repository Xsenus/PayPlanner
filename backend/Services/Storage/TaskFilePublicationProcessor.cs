using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Notifications;

namespace PayPlanner.Api.Services.Storage;

public interface ITaskFilePublicationProcessor
{
    bool Enabled { get; }
    bool NotificationsEnabled { get; }
    Task EnsureScheduledAsync(CancellationToken ct);
    Task<string> ProcessClaimedAsync(Guid operationId, string workerId, CancellationToken ct);
    Task<string> ProcessNotificationClaimedAsync(Guid operationId, string workerId, CancellationToken ct);
}

public sealed class TaskFilePublicationProcessor(PaymentContext db, IManagedTaskFileUploadService uploads,
    ITaskFilePublicationAuthorizationService authorization, IStoragePolicyService policies,
    IOptionsMonitor<StorageOrchestrationOptions> options, TimeProvider timeProvider,
    ILogger<TaskFilePublicationProcessor> logger, INotificationService? notifications = null) : ITaskFilePublicationProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool Enabled => options.CurrentValue.Enabled && options.CurrentValue.AutoTaskFilePublicationEnabled && uploads.Enabled;
    public bool NotificationsEnabled => Enabled && notifications is not null;
    internal sealed record Payload(Guid CommitOperationId);
    private sealed record ResultPayload(Guid PublicationOperationId);
    internal static StorageOperation CreateOperation(StorageOperation commit, StorageObjectGeneration generation, DateTime now)
        => new()
        {
            StorageObjectId = commit.StorageObjectId,
            StorageObjectGeneration = generation,
            OperationType = StorageOperationTypes.PublishTaskFile,
            IdempotencyKey = Key(commit.Id),
            PayloadJson = JsonSerializer.Serialize(new Payload(commit.Id), JsonOptions),
            Status = StorageOperationStatuses.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    internal static string Key(Guid commitId) => $"task-file-publication:{commitId:N}";

    public async Task EnsureScheduledAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        // Only pending owners lacking a queue row. The object/generation FK
        // indexes bound this anti-join; no JSON scan or full catalog walk.
        var missing = await db.StorageOperations.AsNoTracking()
            .Where(o => o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
                && o.PayloadJson != null && o.StorageObject.DataClass == "task-file" && o.StorageObject.OwnerType == "task-file"
                && !o.StorageObject.IsDeleted && o.StorageObject.OwnerId.StartsWith("upload:")
                && o.StorageObjectGenerationId != null
                && !db.StorageOperations.Any(q => q.StorageObjectId == o.StorageObjectId
                    && q.StorageObjectGenerationId == o.StorageObjectGenerationId && q.OperationType == StorageOperationTypes.PublishTaskFile))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new { o.Id, o.StorageObjectId, o.StorageObjectGenerationId })
            .Take(Math.Clamp(options.CurrentValue.WorkerBatchSize, 1, 200)).ToArrayAsync(ct);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var added = missing.Select(o => new StorageOperation
        {
            StorageObjectId = o.StorageObjectId,
            StorageObjectGenerationId = o.StorageObjectGenerationId,
            OperationType = StorageOperationTypes.PublishTaskFile,
            IdempotencyKey = Key(o.Id),
            PayloadJson = JsonSerializer.Serialize(new Payload(o.Id), JsonOptions),
            Status = StorageOperationStatuses.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        if (added.Length == 0) return;
        db.StorageOperations.AddRange(added);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (db.Database.CurrentTransaction is null && StorageObjectService.IsIdempotencyCollision(ex))
        {
            // A competing reconciler won. Other rows in this atomic Save can be
            // scheduled on the next bounded iteration; do not detach caller state.
            foreach (var item in added) db.Entry(item).State = EntityState.Detached;
        }
    }

    public async Task<string> ProcessClaimedAsync(Guid operationId, string workerId, CancellationToken ct)
    {
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        var operation = await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationId
            && o.OperationType == StorageOperationTypes.PublishTaskFile && o.Status == StorageOperationStatuses.Running
            && o.LeaseOwner == workerId && o.LeaseExpiresAt > startedAt, ct);
        if (operation is null) return "leaseLost";
        string result;
        try
        {
            if (!Enabled) result = "disabled";
            else
            {
                Payload? payload;
                try { payload = JsonSerializer.Deserialize<Payload>(operation.PayloadJson ?? string.Empty, JsonOptions); }
                catch (JsonException) { payload = null; }
                var commit = payload is null ? null : await db.StorageOperations.AsNoTracking()
                    .SingleOrDefaultAsync(o => o.Id == payload.CommitOperationId && o.OperationType == "commit"
                        && o.StorageObjectId == operation.StorageObjectId && o.StorageObjectGenerationId == operation.StorageObjectGenerationId, ct);
                if (commit is null || operation.StorageObjectGenerationId is null || operation.IdempotencyKey != Key(commit.Id)) result = "invalid";
                else
                {
                    var ready = await ReadinessAsync(commit, ct);
                    result = ready;
                    if (ready is "ready" or "published")
                    {
                        // Avoid a duplicate intent read for the normal new-file path.
                        result = ready == "published" ? (await uploads.VerifyPublishedAsync(commit.Id, ct)).Status : "pending";
                        if (result == "pending")
                        {
                            result = (await uploads.ResumeAsync(commit.Id, async (actorId, taskId, token) =>
                                await HasCurrentLeaseAsync(operation, workerId, token)
                                && await authorization.CanPublishAsync(actorId, taskId, token), ct)).Status;
                            // A legitimate HTTP publisher may win between receipt and
                            // authorization. Acknowledge only its verified transaction.
                            if (result is "forbidden" or "conflict")
                            {
                                var completed = await uploads.VerifyPublishedAsync(commit.Id, ct);
                                if (completed.Status == "uploaded") result = "uploaded";
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task file publication {OperationId} failed and will be retried", operationId);
            result = "unavailable";
        }
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var waiting = result is "pending" or "disabled";
        var attempts = operation.AttemptCount + (waiting ? 0 : 1);
        var permanent = result is "invalid" or "conflict" or "forbidden" or "notFound";
        var status = result == "uploaded" ? StorageOperationStatuses.Succeeded
            : permanent || attempts >= Math.Clamp(options.CurrentValue.WorkerMaxAttempts, 1, 100) ? StorageOperationStatuses.DeadLetter
            : waiting ? StorageOperationStatuses.Pending : StorageOperationStatuses.Failed;
        DateTime? next = status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter ? null
            : now.AddSeconds(waiting ? Math.Clamp(options.CurrentValue.WorkerPollSeconds, 1, 300)
                : Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10))));
        await using var completionTransaction = await db.Database.BeginTransactionAsync(ct);
        var changed = await db.StorageOperations.Where(o => o.Id == operationId && o.Status == StorageOperationStatuses.Running
                && o.LeaseOwner == workerId && o.LeaseExpiresAt == operation.LeaseExpiresAt && o.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status).SetProperty(o => o.AttemptCount, attempts)
                .SetProperty(o => o.NextAttemptAt, next).SetProperty(o => o.LastError, result == "uploaded" ? null : $"Task publication: {result}.")
                .SetProperty(o => o.CompletedAt, status == StorageOperationStatuses.Succeeded || status == StorageOperationStatuses.DeadLetter ? (DateTime?)now : null)
                .SetProperty(o => o.LeaseOwner, (string?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(o => o.UpdatedAt, now), ct);
        if (changed == 1 && status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter)
        {
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = operation.StorageObjectId,
                StorageObjectGenerationId = operation.StorageObjectGenerationId,
                OperationType = StorageOperationTypes.NotifyTaskFilePublication,
                IdempotencyKey = $"task-file-publication-result:{operation.Id:N}",
                PayloadJson = JsonSerializer.Serialize(new ResultPayload(operation.Id), JsonOptions),
                Status = StorageOperationStatuses.Pending,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }
        await completionTransaction.CommitAsync(ct);
        return changed == 1 ? status : "leaseLost";
    }

    public async Task<string> ProcessNotificationClaimedAsync(Guid operationId, string workerId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var operation = await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationId
            && o.OperationType == StorageOperationTypes.NotifyTaskFilePublication && o.Status == StorageOperationStatuses.Running
            && o.LeaseOwner == workerId && o.LeaseExpiresAt > now, ct);
        if (operation is null) return "leaseLost";
        var result = "disabled";
        try
        {
            if (NotificationsEnabled) result = await DeliverResultAsync(operation, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Task file result notification {OperationId} will be retried", operationId);
            result = "unavailable";
        }
        now = timeProvider.GetUtcNow().UtcDateTime;
        var attempts = operation.AttemptCount + (result == "disabled" ? 0 : 1);
        var status = result == "delivered" ? StorageOperationStatuses.Succeeded
            : result == "invalid" || attempts >= Math.Clamp(options.CurrentValue.WorkerMaxAttempts, 1, 100) ? StorageOperationStatuses.DeadLetter
            : result == "disabled" ? StorageOperationStatuses.Pending : StorageOperationStatuses.Failed;
        DateTime? next = status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter ? null
            : now.AddSeconds(result == "disabled" ? Math.Clamp(options.CurrentValue.WorkerPollSeconds, 1, 300)
                : Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10))));
        var changed = await db.StorageOperations.Where(o => o.Id == operationId && o.Status == StorageOperationStatuses.Running
                && o.LeaseOwner == workerId && o.LeaseExpiresAt == operation.LeaseExpiresAt && o.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status).SetProperty(o => o.AttemptCount, attempts)
                .SetProperty(o => o.NextAttemptAt, next).SetProperty(o => o.LastError, result == "delivered" ? null : $"Task notification: {result}.")
                .SetProperty(o => o.CompletedAt, status == StorageOperationStatuses.Succeeded || status == StorageOperationStatuses.DeadLetter ? (DateTime?)now : null)
                .SetProperty(o => o.LeaseOwner, (string?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(o => o.UpdatedAt, now), ct);
        return changed == 1 ? status : "leaseLost";
    }

    private async Task<string> DeliverResultAsync(StorageOperation operation, CancellationToken ct)
    {
        ResultPayload? payload;
        try { payload = JsonSerializer.Deserialize<ResultPayload>(operation.PayloadJson ?? string.Empty, JsonOptions); }
        catch (JsonException) { return "invalid"; }
        var publication = payload is null ? null : await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == payload.PublicationOperationId
            && o.OperationType == StorageOperationTypes.PublishTaskFile && o.StorageObjectId == operation.StorageObjectId
            && o.StorageObjectGenerationId == operation.StorageObjectGenerationId, ct);
        if (publication is null || operation.IdempotencyKey != $"task-file-publication-result:{publication.Id:N}"
            || publication.Status is not (StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter)) return "invalid";
        Payload? commitPayload;
        try { commitPayload = JsonSerializer.Deserialize<Payload>(publication.PayloadJson ?? string.Empty, JsonOptions); }
        catch (JsonException) { return "invalid"; }
        var commit = commitPayload is null ? null : await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == commitPayload.CommitOperationId
            && o.OperationType == "commit" && o.StorageObjectId == publication.StorageObjectId
            && o.StorageObjectGenerationId == publication.StorageObjectGenerationId, ct);
        if (commit is null || publication.IdempotencyKey != Key(commit.Id)) return "invalid";
        JsonDocument intent;
        try { intent = JsonDocument.Parse(commit.PayloadJson ?? string.Empty); }
        catch (JsonException) { return "invalid"; }
        using (intent)
        {
            var root = intent.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("taskId", out var taskValue)
                || taskValue.ValueKind != JsonValueKind.Number || !taskValue.TryGetInt32(out var taskId) || taskId <= 0
                || !root.TryGetProperty("actorId", out var actorValue) || actorValue.ValueKind != JsonValueKind.Number
                || !actorValue.TryGetInt32(out var actorId) || actorId <= 0) return "invalid";
            var prefix = $"write:task-file:{taskId}:{actorId}:";
            if (commit.IdempotencyKey.Length != prefix.Length + 39 || !commit.IdempotencyKey.StartsWith(prefix, StringComparison.Ordinal)
                || !commit.IdempotencyKey.EndsWith(":commit", StringComparison.Ordinal)
                || !Guid.TryParseExact(commit.IdempotencyKey[prefix.Length..^7], "N", out var uploadKey) || uploadKey == Guid.Empty) return "invalid";
            // A changed/deleted/inaccessible task must not leak a title or file name.
            if (!await authorization.CanPublishAsync(actorId, taskId, ct)) return "delivered";
            var receipt = await uploads.VerifyPublishedAsync(commit.Id, ct);
            if (!NotificationsEnabled || receipt.Status == "disabled") return "disabled";
            var succeeded = receipt.Status == "uploaded";
            // A manual retry may have finished after the queue reached DeadLetter.
            // Conversely, a removed/corrupt former success is not a new failure alert.
            if (!succeeded && publication.Status == StorageOperationStatuses.Succeeded) return "delivered";
            if (!await HasCurrentLeaseAsync(operation, operation.LeaseOwner!, ct)) return "leaseLost";
            await notifications!.NotifyAsync(new NotificationEventEnvelope(actorId, UserNotificationTypes.TaskChanged,
                operation.IdempotencyKey, succeeded ? "Файл прикреплён к задаче" : "Не удалось прикрепить файл к задаче",
                succeeded ? "Загрузка завершена. Файл доступен в карточке задачи."
                    : "Загрузка не завершена. Откройте карточку задачи и проверьте состояние ваших загрузок.",
                "task", taskId, taskId, succeeded ? "emerald" : "rose",
                Metadata: new { uploadKey, result = succeeded ? "uploaded" : "failed" }), ct);
            return "delivered";
        }
    }

    private async Task<bool> HasCurrentLeaseAsync(StorageOperation operation, string workerId, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var owned = db.StorageOperations.Where(o => o.Id == operation.Id && o.Status == StorageOperationStatuses.Running
            && o.LeaseOwner == workerId && o.LeaseExpiresAt == operation.LeaseExpiresAt && o.LeaseExpiresAt > now);
        // The second authorization runs in the domain transaction under the task
        // lock. Lock its queue row too so another claimant cannot replace it there.
        return db.Database.CurrentTransaction is null ? await owned.AnyAsync(ct)
            : await owned.ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, o => o.UpdatedAt), ct) == 1;
    }

    private async Task<string> ReadinessAsync(StorageOperation commit, CancellationToken ct)
    {
        if (commit.Status != StorageOperationStatuses.Succeeded) return "pending";
        JsonDocument intent;
        try { intent = JsonDocument.Parse(commit.PayloadJson ?? string.Empty); }
        catch (JsonException) { return "invalid"; }
        using (intent)
        {
            if (intent.RootElement.ValueKind != JsonValueKind.Object) return "invalid";
            // Published intents need an exact read-only receipt, not a new permission grant.
            if (intent.RootElement.TryGetProperty("fileId", out var fileId) && fileId.ValueKind == JsonValueKind.Number) return "published";
            var taskReady = await ObjectReadinessAsync(commit, "task-file", ct);
            if (taskReady != "ready") return taskReady;
            if (!intent.RootElement.TryGetProperty("caseId", out var caseId) || caseId.ValueKind == JsonValueKind.Null) return "ready";
            if (caseId.ValueKind != JsonValueKind.Number || !caseId.TryGetInt32(out _)) return "invalid";
            if (!options.CurrentValue.ProductWriteEnabledClasses.Contains("case-document", StringComparer.Ordinal)) return "disabled";
            if (!commit.IdempotencyKey.EndsWith(":commit", StringComparison.Ordinal)) return "invalid";
            var caseKey = commit.IdempotencyKey[..^7] + ":case:commit";
            var caseCommit = await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.IdempotencyKey == caseKey && o.OperationType == "commit", ct);
            // A crash before case registration is repaired by ResumeAsync.
            return caseCommit is null ? "ready" : await ObjectReadinessAsync(caseCommit, "case-document", ct);
        }
    }
    private async Task<string> ObjectReadinessAsync(StorageOperation commit, string dataClass, CancellationToken ct)
    {
        if (!options.CurrentValue.ProductWriteEnabledClasses.Contains(dataClass, StringComparer.Ordinal)) return "disabled";
        var policy = policies.Evaluate(dataClass);
        if (!policy.RoutingEnabled || !policy.IsValid) return "disabled";
        if (commit.Status != StorageOperationStatuses.Succeeded) return "pending";
        var generation = await db.StorageObjectGenerations.AsNoTracking().Where(g => g.Id == commit.StorageObjectGenerationId
                && g.StorageObjectId == commit.StorageObjectId && g.StorageObject.DataClass == dataClass
                && !g.StorageObject.IsDeleted && g.StorageObject.CurrentGeneration == g.Generation)
            .Select(g => new { g.Id, g.SizeBytes, g.Sha256 }).SingleOrDefaultAsync(ct);
        if (generation is null) return "conflict";
        var writers = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        var count = await db.StorageReplicas.AsNoTracking().Where(r => r.StorageObjectGenerationId == generation.Id
                && r.Status == StorageReplicaStatuses.Available && r.LastVerifiedAt != null
                && r.SizeBytes == generation.SizeBytes && r.Sha256 == generation.Sha256 && writers.Contains(r.ProviderKey.ToLower()))
            .Select(r => r.ProviderKey.ToLower()).Distinct().CountAsync(ct);
        return count >= policy.RequiredCopies ? "ready" : "pending";
    }
}
