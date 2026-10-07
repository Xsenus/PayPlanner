using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Recovers only bytes already written for a trusted generated intent. Missing or
// uncertain bytes remain pending; this worker never invokes a renderer or PUT.
public sealed class GeneratedCaseReservationRecovery(PaymentContext db, IManagedCaseDocumentUploadService uploads,
    ICaseDocumentStorage files, IOptionsMonitor<StorageOrchestrationOptions> options, TimeProvider clock, ILogger logger,
    GeneratedWorkOrderDocumentPublication workOrders, GeneratedTemplateDocumentPublication templates,
    GeneratedCourtDocumentPublication courts)
{
    private sealed record Original(string Key, string Resource, string Group, string Binding, Guid ObjectId,
        string Locator, long Bytes, string Json);
    private bool Enabled => options.CurrentValue.Enabled && options.CurrentValue.AutoCaseDocumentPublicationEnabled
        && options.CurrentValue.UploadCapacityChecksEnabled && uploads.Enabled;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (!Enabled || db.Database.CurrentTransaction is not null) return 0;
        var now = Now();
        var candidates = await Due(now).TagWith("generated-case-reservation-recovery-window")
            .OrderBy(r => r.GeneratedRecoveryNextAttemptAt).ThenBy(r => r.IdempotencyKey)
            .Select(r => new Original(r.IdempotencyKey, r.ResourceKey, r.GroupId, r.BindingHash,
                r.ObjectId, r.LocalLocator, r.Bytes, r.ProductContextJson!))
            .Take(Math.Clamp(options.CurrentValue.WorkerBatchSize, 1, 10)).ToArrayAsync(ct);
        var claimed = 0;
        foreach (var original in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!Enabled) break;
            now = Now();
            var token = Guid.NewGuid();
            var expires = now.AddSeconds(Math.Clamp(options.CurrentValue.WorkerLeaseSeconds, 15, 3600));
            if (await Match(Due(now), original).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.GeneratedRecoveryLeaseToken, token)
                    .SetProperty(r => r.GeneratedRecoveryLeaseExpiresAt, expires)
                    .SetProperty(r => r.GeneratedRecoveryNextAttemptAt, expires)
                    .SetProperty(r => r.GeneratedRecoveryAttemptCount, r => r.GeneratedRecoveryAttemptCount + 1), ct) != 1) continue;
            claimed++;
            var status = "unavailable";
            try { status = await RecoverAsync(original, token, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning(error, "Generated reservation recovery failed for {ObjectId}", original.ObjectId); }
            // Cancellation retains the lease until expiry; an old execution cannot
            // clear a later token or overwrite its result.
            var retryAt = Now().AddMinutes(status is "forbidden" or "invalid" or "conflict" or "retired" ? 15 : 1);
            await Match(db.StorageCapacityReservations, original).Where(r => r.GeneratedRecoveryLeaseToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.GeneratedRecoveryLeaseToken, (Guid?)null)
                    .SetProperty(r => r.GeneratedRecoveryLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(r => r.GeneratedRecoveryNextAttemptAt, retryAt)
                    .SetProperty(r => r.GeneratedRecoveryLastStatus, status), ct);
        }
        return claimed;
    }

    private IQueryable<StorageCapacityReservation> Due(DateTime now)
    {
        var local = StorageCapacityAdmission.ResourceKey("local", null);
        return db.StorageCapacityReservations.AsNoTracking().Where(r => r.ResourceKey == local
            && r.Status == StorageCapacityReservationStatuses.Pending && r.ProductContextJson != null
            && r.IdempotencyKey.StartsWith("write:case-document:")
            && (r.ProductContextJson.Contains("\"kind\":\"template\"")
                || r.ProductContextJson.Contains("\"kind\":\"work-order\"")
                || r.ProductContextJson.Contains("\"kind\":\"court\""))
            && r.GeneratedRecoveryAttemptCount >= 0 && r.GeneratedRecoveryAttemptCount < int.MaxValue
            && r.GeneratedRecoveryNextAttemptAt <= now
            && (r.GeneratedRecoveryLeaseToken == null && r.GeneratedRecoveryLeaseExpiresAt == null
                || r.GeneratedRecoveryLeaseToken != null && r.GeneratedRecoveryLeaseExpiresAt != null
                    && r.GeneratedRecoveryLeaseExpiresAt <= now)
            && !db.StorageOperations.Any(o => o.IdempotencyKey == r.IdempotencyKey.Replace(":capacity", ":commit")));
    }

    private static IQueryable<StorageCapacityReservation> Match(IQueryable<StorageCapacityReservation> query, Original original)
        => query.Where(r => r.IdempotencyKey == original.Key && r.ResourceKey == original.Resource
            && r.ObjectId == original.ObjectId && r.GroupId == original.Group && r.BindingHash == original.Binding
            && r.LocalLocator == original.Locator && r.Bytes == original.Bytes && r.ProductContextJson == original.Json);

    private async Task<bool> CurrentLeaseAsync(Original original, Guid token, CancellationToken ct)
    {
        if (!Enabled) return false;
        var now = Now();
        var commitKey = original.Key.Replace(":capacity", ":commit", StringComparison.Ordinal);
        var owned = Match(db.StorageCapacityReservations, original).Where(r => r.GeneratedRecoveryLeaseToken == token
            && r.GeneratedRecoveryLeaseExpiresAt > now
            && (r.Status == StorageCapacityReservationStatuses.Pending
                || r.Status == StorageCapacityReservationStatuses.Released && db.StorageOperations.Any(o => o.IdempotencyKey == commitKey
                    && o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
                    && o.StorageObjectId == original.ObjectId && !o.StorageObject.IsDeleted
                    && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
                    && o.StorageObject.CurrentGeneration == 1 && o.StorageObjectGenerationId != null
                    && o.StorageObjectGeneration!.StorageObjectId == original.ObjectId && o.StorageObjectGeneration.Generation == 1)));
        // Lock this execution in the short domain transaction, including after
        // catalog registration has released its separate capacity debt.
        return db.Database.CurrentTransaction is null ? await owned.AnyAsync(ct)
            : await owned.ExecuteUpdateAsync(s => s.SetProperty(r => r.GeneratedRecoveryLeaseExpiresAt,
                r => r.GeneratedRecoveryLeaseExpiresAt), ct) == 1;
    }

    private async Task<string> RecoverAsync(Original original, Guid token, CancellationToken ct)
    {
        var intent = ManagedCaseDocumentUploadService.ParseIntent(original.Json);
        if (!ManagedCaseDocumentUploadService.IsValidResumeIntent(intent) || intent!.Generation is null
            || intent.DocumentId.HasValue || original.ObjectId == Guid.Empty
            || original.Key != StorageObjectService.GetCapacityReservationKey($"case-document:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}"))
            return "invalid";
        Func<int, int, ManagedCaseDocumentGeneration, CancellationToken, Task<bool>> authorize;
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persist;
        Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>> sourceAuthority;
        switch (intent.Generation.Kind)
        {
            case "template":
                if (!GeneratedTemplateDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = templates.CanPublishAsync; persist = templates.PersistAsync; sourceAuthority = templates.CanPublishAsync;
                break;
            case "work-order":
                if (!GeneratedWorkOrderDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = workOrders.CanPublishAsync; persist = workOrders.PersistAsync; sourceAuthority = workOrders.CanPublishAsync;
                break;
            case "court":
                if (!GeneratedCourtDocumentPublication.SupportsIntent(intent)) return "invalid";
                authorize = courts.CanPublishAsync; persist = courts.PersistAsync; sourceAuthority = courts.CanPublishAsync;
                break;
            default: return "invalid";
        }
        async Task<bool> Authorize(int actor, int caseId, CancellationToken cancellation)
            => actor == intent.ActorId && caseId == intent.CaseId && await CurrentLeaseAsync(original, token, cancellation)
                && await authorize(actor, caseId, intent.Generation, cancellation);
        if (!await CurrentLeaseAsync(original, token, ct)) return "leaseLost";
        var result = await uploads.TryResumeGeneratedAsync(intent.CaseId, intent.ActorId, intent.UploadKey,
            intent.Generation.Kind, intent.Generation.RequestSha256, Authorize, persist,
            (path, publish, cancellation) => CaseDocumentAtomicSave.SaveAsync(db, files, path,
                () => Task.CompletedTask, async () =>
                {
                    var document = await publish();
                    if (!await Authorize(intent.ActorId, intent.CaseId, cancellation))
                        throw new InvalidOperationException("Generated reservation recovery lost its lease or publication authority.");
                    return document;
                }, cancellation, preserveFileOnFailure: true), ct,
            async (source, cancellation) => await CurrentLeaseAsync(original, token, cancellation)
                && await sourceAuthority(source, cancellation));
        return result?.Status ?? "invalid";
    }

    private DateTime Now() => DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
