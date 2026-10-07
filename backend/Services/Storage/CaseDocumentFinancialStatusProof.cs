using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Services.Storage;

// Bounded metadata proof for authorized status reads. Never loads frozen graphs
// or HTML and never invokes providers; native winners are checked in the same SQL.
internal sealed record CaseDocumentFinancialStatusProof(GeneratedFinancialDocumentProof Proof,
    string RequestSha256, string ActorName, int? DocumentId, bool HasWinner,
    int RecoveryAttemptCount, string? RecoveryLastStatus)
{
    internal static Task<Dictionary<Guid, CaseDocumentFinancialStatusProof>> ReadAsync(PaymentContext db,
        int caseId, int actorId, Guid[] keys, CancellationToken ct)
        => db.GeneratedFinancialDocumentAttempts.AsNoTracking().TagWith("case-upload-status-financial-proofs")
            .Where(a => a.ClientCaseId == caseId && a.ActorId == actorId && keys.Contains(a.UploadKey))
            .Select(a => new CaseDocumentFinancialStatusProof(
                new GeneratedFinancialDocumentProof(1, a.Id, a.UploadKey, a.ClientCaseId, a.WorkOrderId,
                    a.ActorId, a.Kind, a.EntityId, a.InvoiceEntityId, a.FrozenDomainSha256,
                    a.ContentSizeBytes, a.ContentSha256), a.RequestSha256, a.ActorName, a.PublishedDocumentId,
                a.PublishedDocumentId != null && (
                    a.Kind == "invoice" && db.Payments.Any(p => p.Id == a.EntityId
                        && p.InvoiceId == a.InvoiceEntityId && p.ClientCaseId == caseId
                        && p.InvoiceClientCases.Any(link => link.ClientCaseId == caseId))
                    || a.Kind == "act" && db.Acts.Any(act => act.Id == a.EntityId)
                    || a.Kind == "contract" && db.Contracts.Any(c => c.Id == a.EntityId
                        && c.ClientCaseContracts.Any(link => link.ClientCaseId == caseId))),
                a.RecoveryAttemptCount, a.RecoveryLastStatus))
            .ToDictionaryAsync(a => a.Proof.UploadKey, ct);

    internal bool Matches(ManagedCaseDocumentUploadService.UploadIntent? intent)
        => intent is { Generation: { } generation }
            && GeneratedFinancialDocumentPublication.SupportsIntent(intent)
            && GeneratedFinancialDocumentRegistry.ReadProof(generation) == Proof
            && generation.RequestSha256 == RequestSha256 && intent.ActorName == ActorName;
}
