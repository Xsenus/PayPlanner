using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Technical reservations only. All callers acquire case, work-order and then
// number-scope locks in that order; no storage/provider I/O belongs here.
public sealed class GeneratedFinancialClaims(PaymentContext db)
{
    public async Task LockNumberScopeAsync(GeneratedFinancialDocumentKind kind, CancellationToken ct)
    {
        RequireTransaction();
        var id = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => 1,
            GeneratedFinancialDocumentKind.Act => 2,
            GeneratedFinancialDocumentKind.Contract => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (await db.GeneratedFinancialNumberScopes.Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Version, p => p.Version), ct) != 1)
            throw new InvalidOperationException("Missing financial number scope.");
    }

    public async Task<string> NextNumberAsync(GeneratedFinancialDocumentKind kind, int caseId, int workOrderId,
        DateTime date, string? requestedNumber, CancellationToken ct)
    {
        await LockNumberScopeAsync(kind, ct);
        var name = KindName(kind); var dateKey = DateKey(date);
        var prefix = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => "WO",
            GeneratedFinancialDocumentKind.Act => "WO-ACT",
            _ => "WO-CONTRACT"
        };
        var basis = string.IsNullOrWhiteSpace(requestedNumber) ? $"{prefix}-{caseId}-{workOrderId}" : requestedNumber.Trim();
        // Preserve the first available suffix, but query 128 candidates at once
        // instead of one database round trip for each occupied number.
        for (long first = 1; first <= int.MaxValue; first += 128)
        {
            var candidates = Enumerable.Range(0, (int)Math.Min(128, int.MaxValue - first + 1))
                .Select(offset => Candidate(basis, first + offset)).ToArray();
            var ledger = await LedgerNumbersAsync(kind, date, candidates, ct);
            var pending = await LiveNumberClaims().Where(c => c.Kind == name && c.DateKey == dateKey && candidates.Contains(c.Number))
                .Select(c => c.Number).ToArrayAsync(ct);
            var occupied = ledger.Concat(pending).ToHashSet(StringComparer.Ordinal);
            var available = candidates.FirstOrDefault(n => !occupied.Contains(n));
            if (available is not null) return available;
        }
        throw new InvalidOperationException("Financial document number suffixes are exhausted.");
    }

    public async Task<bool> ClaimAsync(GeneratedFinancialDocumentAttempt attempt, GeneratedFinancialDomainSnapshot domain, CancellationToken ct)
    {
        RequireTransaction();
        if (domain.Number.Length is < 1 or > 120) return false;
        var dateKey = DateKey(domain.Date); var kind = KindName(domain.Kind);
        if ((await LedgerNumbersAsync(domain.Kind, domain.Date, [domain.Number], ct)).Length != 0
            || await LiveNumberClaims().AnyAsync(c => c.Kind == kind && c.DateKey == dateKey && c.Number == domain.Number, ct)) return false;
        var sourceKeys = domain.Lines.Select(l => SourceKey(domain.WorkOrderId, l.WorkOrderItemId)).ToArray();
        if (domain.Kind == GeneratedFinancialDocumentKind.Act && (await UsedActSourceKeysAsync(sourceKeys, ct)).Length != 0) return false;
        // A completed or explicitly retired attempt keeps its frozen evidence.
        // Only its obsolete technical number claim is replaced, inside this transaction.
        await db.GeneratedFinancialNumberClaims.Where(c => c.Kind == kind && c.DateKey == dateKey && c.Number == domain.Number
            && db.GeneratedFinancialDocumentAttempts.Any(a => a.Id == c.AttemptId && (a.PublishedDocumentId != null
                || db.CaseDocumentUploadRetirements.Any(r => r.CaseId == a.ClientCaseId && r.ActorId == a.ActorId && r.UploadKey == a.UploadKey))))
            .ExecuteDeleteAsync(ct);
        db.GeneratedFinancialNumberClaims.Add(new() { Attempt = attempt, AttemptId = attempt.Id, Kind = kind, DateKey = dateKey, Number = domain.Number });
        if (domain.Kind == GeneratedFinancialDocumentKind.Act)
            db.GeneratedFinancialActLineClaims.AddRange(domain.Lines.Select(l => new GeneratedFinancialActLineClaim
            { Attempt = attempt, AttemptId = attempt.Id, WorkOrderItemId = l.WorkOrderItemId, SourceKey = SourceKey(domain.WorkOrderId, l.WorkOrderItemId) }));
        return true;
    }

    public async Task<string[]> UsedActSourceKeysAsync(string[] sourceKeys, CancellationToken ct)
    {
        var ledger = await db.Set<ActLineItem>().AsNoTracking().Where(l => sourceKeys.Contains(l.SourceKey)
            && l.Act.WorkflowStatus != ActWorkflowStatus.Cancelled && l.Act.Status != ActStatus.Terminated).Select(l => l.SourceKey).ToArrayAsync(ct);
        var pending = await db.GeneratedFinancialActLineClaims.AsNoTracking().Where(c => sourceKeys.Contains(c.SourceKey)
            && c.Attempt.PublishedDocumentId == null && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == c.Attempt.ClientCaseId
                && r.ActorId == c.Attempt.ActorId && r.UploadKey == c.Attempt.UploadKey)).Select(c => c.SourceKey).ToArrayAsync(ct);
        return ledger.Concat(pending).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal async Task<bool> OwnsAsync(GeneratedFinancialDocumentAttempt attempt, GeneratedFinancialDomainSnapshot domain, CancellationToken ct)
    {
        if (attempt.PublishedDocumentId.HasValue) return attempt.PublishedDocumentId > 0;
        var kind = KindName(domain.Kind); var date = DateKey(domain.Date);
        if (!await db.GeneratedFinancialNumberClaims.AsNoTracking().AnyAsync(c => c.AttemptId == attempt.Id
            && c.Kind == kind && c.DateKey == date && c.Number == domain.Number, ct)) return false;
        if (domain.Kind != GeneratedFinancialDocumentKind.Act) return true;
        var keys = await db.GeneratedFinancialActLineClaims.AsNoTracking().Where(c => c.AttemptId == attempt.Id)
            .Select(c => new { c.WorkOrderItemId, c.SourceKey }).ToArrayAsync(ct);
        var expected = domain.Lines.Select(l => (l.WorkOrderItemId, SourceKey(domain.WorkOrderId, l.WorkOrderItemId))).ToHashSet();
        return keys.Length == expected.Count && keys.All(k => expected.Contains((k.WorkOrderItemId, k.SourceKey)));
    }

    internal async Task<bool> CanPublishAsync(GeneratedFinancialDocumentAttempt attempt, GeneratedFinancialDomainSnapshot domain, CancellationToken ct)
    {
        if (!await OwnsAsync(attempt, domain, ct)) return false;
        if (attempt.PublishedDocumentId.HasValue) return true;
        if ((await LedgerNumbersAsync(domain.Kind, domain.Date, [domain.Number], ct)).Length != 0) return false;
        if (domain.Kind != GeneratedFinancialDocumentKind.Act) return true;
        var keys = domain.Lines.Select(l => SourceKey(domain.WorkOrderId, l.WorkOrderItemId)).ToArray();
        return !await db.Set<ActLineItem>().AsNoTracking().AnyAsync(l => keys.Contains(l.SourceKey)
            && l.Act.WorkflowStatus != ActWorkflowStatus.Cancelled && l.Act.Status != ActStatus.Terminated, ct)
            && !await db.GeneratedFinancialActLineClaims.AsNoTracking().AnyAsync(c => c.AttemptId != attempt.Id
                && keys.Contains(c.SourceKey) && c.Attempt.PublishedDocumentId == null
                && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == c.Attempt.ClientCaseId
                    && r.ActorId == c.Attempt.ActorId && r.UploadKey == c.Attempt.UploadKey), ct);
    }

    private IQueryable<GeneratedFinancialNumberClaim> LiveNumberClaims() => db.GeneratedFinancialNumberClaims.AsNoTracking()
        .Where(c => c.Attempt.PublishedDocumentId == null && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == c.Attempt.ClientCaseId
            && r.ActorId == c.Attempt.ActorId && r.UploadKey == c.Attempt.UploadKey));
    private Task<string[]> LedgerNumbersAsync(GeneratedFinancialDocumentKind kind, DateTime date, string[] candidates, CancellationToken ct)
        => kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => db.Invoices.AsNoTracking().Where(i => i.Date.Date == date.Date && candidates.Contains(i.Number)).Select(i => i.Number).ToArrayAsync(ct),
            GeneratedFinancialDocumentKind.Act => db.Acts.AsNoTracking().Where(i => i.Date.Date == date.Date && candidates.Contains(i.Number)).Select(i => i.Number).ToArrayAsync(ct),
            GeneratedFinancialDocumentKind.Contract => db.Contracts.AsNoTracking().Where(i => i.Date.Date == date.Date && candidates.Contains(i.Number)).Select(i => i.Number).ToArrayAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    private void RequireTransaction()
    { if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Financial claims require their capture/publication transaction."); }
    internal static string DateKey(DateTime date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string SourceKey(int workOrderId, int itemId) => $"work-order:{workOrderId}:item:{itemId}";
    private static string KindName(GeneratedFinancialDocumentKind kind) => kind switch
    { GeneratedFinancialDocumentKind.Invoice => "invoice", GeneratedFinancialDocumentKind.Act => "act", GeneratedFinancialDocumentKind.Contract => "contract", _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static string Candidate(string basis, long ordinal)
    {
        var suffix = ordinal == 1 ? "" : "-" + ordinal.ToString(CultureInfo.InvariantCulture);
        var maximum = 120 - suffix.Length;
        return (basis.Length > maximum ? basis[..maximum] : basis) + suffix;
    }
}
