using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Recovers the acceptance-to-file-intent gap. A file commit is subsequently
// handled by the existing leased publication queue, never by a second renderer.
public sealed class GeneratedFinancialAttemptRecovery(PaymentContext db,
    GeneratedFinancialDocumentRegistry sources, GeneratedFinancialDocumentPublication financials,
    IManagedCaseDocumentUploadService uploads, ICaseDocumentStorage files,
    IOptionsMonitor<StorageOrchestrationOptions> options, TimeProvider clock,
    ILogger<GeneratedFinancialAttemptRecovery> logger)
{
    public bool Enabled => options.CurrentValue.Enabled && options.CurrentValue.WorkerEnabled
        && options.CurrentValue.AutoCaseDocumentPublicationEnabled && options.CurrentValue.UploadCapacityChecksEnabled
        && uploads.Enabled;

    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        if (!Enabled) return 0;
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Financial recovery requires its own scope.");
        var now = Now();
        var candidates = await Eligible().TagWith("financial-attempt-recovery-window")
            .Where(a => a.RecoveryNextAttemptAt <= now
                && (a.RecoveryLeaseExpiresAt == null || a.RecoveryLeaseExpiresAt <= now))
            .OrderBy(a => a.RecoveryNextAttemptAt).ThenBy(a => a.Id).Select(a => a.Id)
            .Take(Math.Clamp(options.CurrentValue.WorkerBatchSize, 1, 10)).ToArrayAsync(ct);
        var claimed = 0;
        foreach (var id in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!Enabled) break;
            now = Now();
            var token = Guid.NewGuid();
            var expires = now.AddSeconds(Math.Clamp(options.CurrentValue.WorkerLeaseSeconds, 15, 3600));
            if (await Eligible().Where(a => a.Id == id && a.RecoveryNextAttemptAt <= now
                    && (a.RecoveryLeaseExpiresAt == null || a.RecoveryLeaseExpiresAt <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RecoveryLeaseToken, token)
                    .SetProperty(a => a.RecoveryLeaseExpiresAt, expires)
                    .SetProperty(a => a.RecoveryNextAttemptAt, expires)
                    .SetProperty(a => a.RecoveryAttemptCount, a => a.RecoveryAttemptCount + 1), ct) != 1) continue;
            claimed++;
            var status = "unavailable";
            try { status = await RecoverAsync(id, token, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "Accepted financial attempt recovery failed for {AttemptId}", id); }
            // Cancellation leaves the durable lease to expire. A stale worker
            // cannot release a later execution token or replace its outcome.
            var retryAt = Now().AddMinutes(status is "forbidden" or "invalid" or "conflict" ? 15 : 1);
            await db.GeneratedFinancialDocumentAttempts.Where(a => a.Id == id && a.RecoveryLeaseToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.RecoveryLeaseToken, (Guid?)null)
                    .SetProperty(a => a.RecoveryLeaseExpiresAt, (DateTime?)null)
                    .SetProperty(a => a.RecoveryNextAttemptAt, retryAt)
                    .SetProperty(a => a.RecoveryLastStatus, status), ct);
        }
        return claimed;
    }

    private IQueryable<GeneratedFinancialDocumentAttempt> Eligible()
        => db.GeneratedFinancialDocumentAttempts.AsNoTracking()
            .Where(a => a.PublishedDocumentId == null
                && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == a.ClientCaseId
                    && r.ActorId == a.ActorId && r.UploadKey == a.UploadKey)
                && !db.StorageOperations.Any(o => o.OperationType == "commit"
                    && o.IdempotencyKey == "write:case-document:" + a.ClientCaseId + ":" + a.ActorId + ":" + a.UploadKeyText + ":commit"));

    private async Task<string> RecoverAsync(Guid id, Guid leaseToken, CancellationToken ct)
    {
        var scope = await db.GeneratedFinancialDocumentAttempts.AsNoTracking().Where(a => a.Id == id)
            .Select(a => new { a.ClientCaseId, a.ActorId, a.UploadKey }).SingleOrDefaultAsync(ct);
        if (scope is null || !await CurrentLeaseAsync(id, leaseToken, ct)) return "leaseLost";
        var headers = await CaseDocumentFinancialStatusProof.ReadAsync(db, scope.ClientCaseId, scope.ActorId, [scope.UploadKey], ct);
        if (!headers.TryGetValue(scope.UploadKey, out var header) || header.DocumentId.HasValue
            || GeneratedFinancialDocumentRegistry.MetadataReceipt(header.Proof, header.RequestSha256) is not { } receipt)
            return "invalid";
        async Task<bool> Authorize(int actorId, int caseId, CancellationToken token)
            => actorId == scope.ActorId && caseId == scope.ClientCaseId
                && await CurrentLeaseAsync(id, leaseToken, token)
                && await financials.CanPublishAsync(actorId, caseId, receipt.Generation, token);
        if (!await Authorize(scope.ActorId, scope.ClientCaseId, ct)) return "forbidden";
        ManagedCaseDocumentPublication atomic = (path, publish, token) => CaseDocumentAtomicSave.SaveAsync(db, files, path,
            () => Task.CompletedTask, async () =>
            {
                var document = await publish();
                if (!await Authorize(scope.ActorId, scope.ClientCaseId, token))
                    throw new InvalidOperationException("Financial recovery lost its lease or publication authority.");
                return document;
            }, token, preserveFileOnFailure: true);
        var result = await uploads.TryResumeGeneratedAsync(scope.ClientCaseId, scope.ActorId, scope.UploadKey,
            "financial", receipt.Generation.RequestSha256, Authorize, financials.PersistAsync, atomic, ct,
            financials.CanPublishAsync);
        if (result is not null && (result.Status != "unavailable" || await sources.HasFileCommitAsync(receipt, ct)))
            return result.Status;
        var frozen = await sources.ReadFrozenAsync(receipt, ct);
        if (frozen is null || !await Authorize(scope.ActorId, scope.ClientCaseId, ct)) return "forbidden";
        using var stream = new MemoryStream(frozen.Bytes, writable: false);
        var name = GeneratedFinancialDocumentRegistry.FileName(receipt.Proof);
        var file = new FormFile(stream, 0, frozen.Bytes.Length, "file", name)
        { Headers = new HeaderDictionary(), ContentType = "text/html; charset=utf-8" };
        return (await uploads.GenerateAsync(new(scope.ClientCaseId, scope.ActorId, frozen.Domain.ActorName,
            scope.UploadKey, file, name, GeneratedFinancialDocumentRegistry.DocumentType(receipt.Proof.Kind)!, "Черновик",
            GeneratedFinancialDocumentRegistry.DocumentKey(receipt.Proof)), receipt.Generation,
            Authorize, financials.PersistAsync, atomic, ct, financials.CanPublishAsync)).Status;
    }

    private Task<bool> CurrentLeaseAsync(Guid id, Guid token, CancellationToken ct)
    {
        if (!Enabled) return Task.FromResult(false);
        var now = Now();
        return db.GeneratedFinancialDocumentAttempts.AsNoTracking().AnyAsync(a => a.Id == id
            && a.RecoveryLeaseToken == token && a.RecoveryLeaseExpiresAt > now
            && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == a.ClientCaseId
                && r.ActorId == a.ActorId && r.UploadKey == a.UploadKey), ct);
    }

    private DateTime Now() => DateTime.SpecifyKind(clock.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified);
}
