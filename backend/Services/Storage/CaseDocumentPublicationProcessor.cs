using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public interface ICaseDocumentPublicationProcessor
{
    bool Enabled { get; }
    Task EnsureScheduledAsync(CancellationToken ct);
    Task<string> ProcessClaimedAsync(Guid operationId, string workerId, CancellationToken ct);
}

public sealed class CaseDocumentPublicationProcessor(PaymentContext db, IManagedCaseDocumentUploadService uploads,
    CaseDocumentPublicationRecords records, CaseDocumentNotificationService notifications, ICaseDocumentStorage files,
    IStoragePolicyService policies, IOptionsMonitor<StorageOrchestrationOptions> options, TimeProvider clock,
    ILogger<CaseDocumentPublicationProcessor> logger,
    GeneratedWorkOrderDocumentPublication workOrders, GeneratedTemplateDocumentPublication templates,
    GeneratedCourtDocumentPublication courts, GeneratedFinancialDocumentPublication financials) : ICaseDocumentPublicationProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool Enabled => options.CurrentValue.Enabled && options.CurrentValue.AutoCaseDocumentPublicationEnabled
        && options.CurrentValue.UploadCapacityChecksEnabled && uploads.Enabled;
    private sealed record Payload(Guid CommitOperationId);
    internal static string Key(Guid commitId) => $"case-document-publication:{commitId:N}";
    internal static bool ReferencesCommit(string? json, Guid commitId)
    {
        try { return JsonSerializer.Deserialize<Payload>(json ?? "", JsonOptions)?.CommitOperationId == commitId; }
        catch (JsonException) { return false; }
    }
    internal static StorageOperation CreateOperation(StorageOperation commit, StorageObjectGeneration generation, DateTime now)
        => new()
        {
            StorageObjectId = commit.StorageObjectId,
            StorageObjectGeneration = generation,
            OperationType = StorageOperationTypes.PublishCaseDocument,
            IdempotencyKey = Key(commit.Id),
            PayloadJson = JsonSerializer.Serialize(new Payload(commit.Id), JsonOptions),
            Status = StorageOperationStatuses.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };

    public async Task EnsureScheduledAsync(CancellationToken ct)
    {
        if (!Enabled) return;
        await new GeneratedCaseReservationRecovery(db, uploads, files, options, clock, logger, workOrders, templates, courts).ProcessBatchAsync(ct);
        // Bounded indexed anti-join, also recovers commits made before the flag was enabled.
        // Task-file case copies have a different pending owner and must never enter this consumer.
        var missing = await db.StorageOperations.AsNoTracking()
            .TagWith("case-document-publication-schedule")
            .Where(o => o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded && o.PayloadJson != null
                && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
                && !o.StorageObject.IsDeleted
                && (o.StorageObject.OwnerId.StartsWith("upload:case:")
                    // Canonical server intents use this token. This is a bounded
                    // candidate filter; typed source proof is required below.
                    || o.StorageObject.OwnerId.StartsWith("generated:case:") && (o.PayloadJson.Contains("\"kind\":\"work-order\"")
                        || o.PayloadJson.Contains("\"kind\":\"template\"") && o.PayloadJson.Contains("templateKey")
                        || o.PayloadJson.Contains("\"kind\":\"court\"") && o.PayloadJson.Contains("contentSha256")
                        || o.PayloadJson.Contains("\"kind\":\"financial\"") && o.PayloadJson.Contains("domainSha256")))
                && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
                && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
                && !db.StorageOperations.Any(q => q.StorageObjectId == o.StorageObjectId
                    && q.StorageObjectGenerationId == o.StorageObjectGenerationId && q.OperationType == StorageOperationTypes.PublishCaseDocument))
            .OrderBy(o => o.CreatedAt).ThenBy(o => o.Id)
            .Select(o => new { o.Id, o.StorageObjectId, o.StorageObjectGenerationId })
            .Take(Math.Clamp(options.CurrentValue.WorkerBatchSize, 1, 200)).ToArrayAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var added = missing.Select(o => new StorageOperation
        {
            StorageObjectId = o.StorageObjectId,
            StorageObjectGenerationId = o.StorageObjectGenerationId,
            OperationType = StorageOperationTypes.PublishCaseDocument,
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
            foreach (var item in added) db.Entry(item).State = EntityState.Detached;
        }
    }

    public async Task<string> ProcessClaimedAsync(Guid operationId, string workerId, CancellationToken ct)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        var operation = await db.StorageOperations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == operationId
            && o.OperationType == StorageOperationTypes.PublishCaseDocument && o.Status == StorageOperationStatuses.Running
            && o.LeaseOwner == workerId && o.LeaseExpiresAt > started, ct);
        if (operation is null) return "leaseLost";
        string result;
        try
        {
            if (!Enabled) result = "disabled";
            else
            {
                Payload? payload;
                try { payload = JsonSerializer.Deserialize<Payload>(operation.PayloadJson ?? "", JsonOptions); }
                catch (JsonException) { payload = null; }
                var commit = payload is null ? null : await db.StorageOperations.AsNoTracking()
                    .SingleOrDefaultAsync(o => o.Id == payload.CommitOperationId && o.OperationType == "commit"
                        && o.StorageObjectId == operation.StorageObjectId && o.StorageObjectGenerationId == operation.StorageObjectGenerationId, ct);
                if (commit is null || operation.StorageObjectGenerationId is null || operation.IdempotencyKey != Key(commit.Id)) result = "invalid";
                else
                {
                    result = await ReadinessAsync(commit, ct);
                    var stored = ManagedCaseDocumentUploadService.ParseIntent(commit.PayloadJson);
                    if ((result is "published" or "ready") && stored?.Generation is not null)
                        result = await ResumeGeneratedAsync(stored, operation, workerId, ct);
                    else if (result == "published") result = (await uploads.VerifyPublishedAsync(commit.Id, ct)).Status;
                    else if (result == "ready")
                    {
                        var intent = ManagedCaseDocumentUploadService.ParseIntent(commit.PayloadJson)!;
                        result = (await uploads.ResumeAsync(commit.Id, async (_, _, token) =>
                            Enabled && await HasCurrentLeaseAsync(operation, workerId, token), async (document, token) =>
                            {
                                var clientCase = await db.ClientCases.SingleAsync(c => c.Id == document.ClientCaseId, token);
                                await records.PersistUploadAsync(clientCase, document, document.Version == 1,
                                    document.UploadedByUserId!.Value, document.UploadedByName,
                                    DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime(), token);
                            }, (path, publish, token) => CaseDocumentAtomicSave.SaveAsync(db, files, path,
                                () => Task.CompletedTask, async () =>
                                {
                                    var published = await publish();
                                    if (!Enabled) throw new PublicationScopeFailure("disabled");
                                    if (!await HasCurrentLeaseAsync(operation, workerId, token)) throw new PublicationScopeFailure("leaseLost");
                                    if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, intent.ActorId, intent.CaseId, token))
                                        throw new PublicationScopeFailure("forbidden");
                                    return published;
                                }, token, preserveFileOnFailure: true), ct)).Status;
                        if (result is "forbidden" or "conflict")
                        {
                            var receipt = await uploads.VerifyPublishedAsync(commit.Id, ct);
                            if (receipt.Status == "uploaded") result = "uploaded";
                        }
                    }
                }
            }
            if (result == "uploaded") await notifications.FlushCommittedAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (PublicationScopeFailure ex) { result = ex.Status; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Case document publication {OperationId} will be retried", operationId);
            result = "unavailable";
        }
        if (!Enabled && result != "uploaded") result = "disabled";
        var now = clock.GetUtcNow().UtcDateTime;
        var waiting = result is "pending" or "disabled";
        var attempts = operation.AttemptCount + (waiting ? 0 : 1);
        var permanent = result is "invalid" or "conflict" or "forbidden" or "notFound" or "retired";
        var status = result == "uploaded" ? StorageOperationStatuses.Succeeded
            : permanent || attempts >= Math.Clamp(options.CurrentValue.WorkerMaxAttempts, 1, 100) ? StorageOperationStatuses.DeadLetter
            : waiting ? StorageOperationStatuses.Pending : StorageOperationStatuses.Failed;
        DateTime? next = status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter ? null
            : now.AddSeconds(waiting ? Math.Clamp(options.CurrentValue.WorkerPollSeconds, 1, 300)
                : Math.Min(3600, 5 * Math.Pow(2, Math.Min(attempts, 10))));
        DateTime? completedAt = status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter ? now : null;
        var changed = await db.StorageOperations.Where(o => o.Id == operationId && o.Status == StorageOperationStatuses.Running
                && o.LeaseOwner == workerId && o.LeaseExpiresAt == operation.LeaseExpiresAt && o.LeaseExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status).SetProperty(o => o.AttemptCount, attempts)
                .SetProperty(o => o.NextAttemptAt, next).SetProperty(o => o.LastError, result == "uploaded" ? null : $"Case publication: {result}.")
                .SetProperty(o => o.CompletedAt, completedAt)
                .SetProperty(o => o.LeaseOwner, (string?)null).SetProperty(o => o.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(o => o.UpdatedAt, now), ct);
        return changed == 1 ? status : "leaseLost";
    }

    private async Task<bool> HasCurrentLeaseAsync(StorageOperation operation, string workerId, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var owned = db.StorageOperations.Where(o => o.Id == operation.Id && o.Status == StorageOperationStatuses.Running
            && o.LeaseOwner == workerId && o.LeaseExpiresAt == operation.LeaseExpiresAt && o.LeaseExpiresAt > now);
        // Lock the queue row in the domain transaction so its lease cannot be replaced before publication commits.
        return db.Database.CurrentTransaction is null ? await owned.AnyAsync(ct)
            : await owned.ExecuteUpdateAsync(s => s.SetProperty(o => o.UpdatedAt, o => o.UpdatedAt), ct) == 1;
    }

    private async Task<string> ResumeGeneratedAsync(ManagedCaseDocumentUploadService.UploadIntent intent,
        StorageOperation operation, string workerId, CancellationToken ct)
    {
        var generation = intent.Generation!;
        Func<int, int, ManagedCaseDocumentGeneration, CancellationToken, Task<bool>> authorize;
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persist;
        switch (generation.Kind)
        {
            case "work-order":
                if (!GeneratedWorkOrderDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = workOrders.CanPublishAsync;
                persist = workOrders.PersistAsync;
                break;
            case "template":
                if (!GeneratedTemplateDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = templates.CanPublishAsync;
                persist = templates.PersistAsync;
                break;
            case "court":
                if (!GeneratedCourtDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = courts.CanPublishAsync;
                persist = courts.PersistAsync;
                break;
            case "financial":
                if (!GeneratedFinancialDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = financials.CanPublishAsync;
                persist = financials.PersistAsync;
                break;
            default: return "disabled";
        }
        var result = await uploads.TryResumeGeneratedAsync(intent.CaseId, intent.ActorId, intent.UploadKey,
            generation.Kind, generation.RequestSha256, async (actor, caseId, token) => Enabled
                && await HasCurrentLeaseAsync(operation, workerId, token)
                && await authorize(actor, caseId, generation, token), persist,
            (path, publish, token) => CaseDocumentAtomicSave.SaveAsync(db, files, path,
                () => Task.CompletedTask, async () =>
                {
                    var published = await publish();
                    if (!Enabled) throw new PublicationScopeFailure("disabled");
                    if (!await HasCurrentLeaseAsync(operation, workerId, token)) throw new PublicationScopeFailure("leaseLost");
                    if (!await authorize(intent.ActorId, intent.CaseId, generation, token))
                        throw new PublicationScopeFailure("forbidden");
                    return published;
                }, token, preserveFileOnFailure: true), ct);
        return result?.Status ?? "invalid";
    }

    private async Task<string> ReadinessAsync(StorageOperation commit, CancellationToken ct)
    {
        if (commit.Status != StorageOperationStatuses.Succeeded) return "pending";
        var intent = ManagedCaseDocumentUploadService.ParseIntent(commit.PayloadJson);
        if (intent is null || !ManagedCaseDocumentUploadService.IsValidResumeIntent(intent)) return "invalid";
        if (commit.IdempotencyKey != StorageObjectService.GetCommitOperationKey($"case-document:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}")) return "conflict";
        if (intent.DocumentId.HasValue) return "published";
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, intent.CaseId, intent.ActorId, intent.UploadKey, ct))
            return "retired";
        var policy = policies.Evaluate("case-document");
        if (!policy.RoutingEnabled || !policy.IsValid) return "disabled";
        var generation = await db.StorageObjectGenerations.AsNoTracking().Where(g => g.Id == commit.StorageObjectGenerationId
                && g.StorageObjectId == commit.StorageObjectId && g.StorageObject.DataClass == "case-document"
                && !g.StorageObject.IsDeleted && g.StorageObject.CurrentGeneration == g.Generation)
            .Select(g => new { g.Id, g.SizeBytes, g.Sha256 }).SingleOrDefaultAsync(ct);
        if (generation is null) return "conflict";
        if (generation.SizeBytes != intent.SizeBytes || generation.Sha256 != intent.Sha256) return "conflict";
        var writers = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        var count = await db.StorageReplicas.AsNoTracking().Where(r => r.StorageObjectGenerationId == generation.Id
                && r.Status == StorageReplicaStatuses.Available && r.LastVerifiedAt != null
                && r.SizeBytes == generation.SizeBytes && r.Sha256 == generation.Sha256 && writers.Contains(r.ProviderKey.ToLower()))
            .Select(r => r.ProviderKey.ToLower()).Distinct().CountAsync(ct);
        return count >= policy.RequiredCopies ? "ready" : "pending";
    }

    private sealed class PublicationScopeFailure(string status) : Exception { public string Status => status; }
}
