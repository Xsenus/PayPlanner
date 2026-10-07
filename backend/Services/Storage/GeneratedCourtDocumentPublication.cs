using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Services.Storage;

public sealed class GeneratedCourtDocumentPublication(PaymentContext db, CaseDocumentPublicationRecords records,
    CourtDocumentSourceRegistry sources)
{
    internal static ManagedCaseDocumentGenerationSource? GenerationSource(ManagedCaseDocumentGeneration generation)
    {
        var snapshot = CourtDocumentSourceRegistry.ReadSnapshot(generation);
        return snapshot is null ? null : new(snapshot.CaseId, snapshot.ActorId, snapshot.AttemptId,
            snapshot.DocumentKey, snapshot.DocumentKey, snapshot.FileName, "text/html; charset=utf-8",
            CourtSyncNormalization.DocumentTypes.CourtEventSummary, "received", generation);
    }

    internal static bool SupportsIntent(ManagedCaseDocumentUploadService.UploadIntent? intent)
    {
        if (!ManagedCaseDocumentUploadService.IsValidResumeIntent(intent) || intent!.Generation is null) return false;
        var expected = GenerationSource(intent.Generation);
        var snapshot = CourtDocumentSourceRegistry.ReadSnapshot(intent.Generation);
        return expected is not null && snapshot is not null
            && expected == ManagedCaseDocumentUploadService.GenerationSource(intent)
            && intent.ActorName == snapshot.ActorName
            && intent.SizeBytes == snapshot.ContentSizeBytes && intent.Sha256 == snapshot.ContentSha256;
    }

    internal static bool CanSchedule(string? json, string owner)
    {
        var intent = ManagedCaseDocumentUploadService.ParseIntent(json);
        return SupportsIntent(intent) && !intent!.DocumentId.HasValue
            && owner == $"generated:case:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}";
    }

    public Task<bool> CanPublishAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
        => sources.CanPublishAsync(source, ct);

    public Task<bool> CanPublishAsync(int actorId, int caseId, ManagedCaseDocumentGeneration generation, CancellationToken ct)
        => GenerationSource(generation) is { } source && source.ActorId == actorId && source.CaseId == caseId
            ? sources.CanPublishAsync(source, ct) : Task.FromResult(false);

    public async Task PersistAsync(ClientCaseDocument document, ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        var source = GenerationSource(generation);
        var snapshot = CourtDocumentSourceRegistry.ReadSnapshot(generation);
        if (db.Database.CurrentTransaction is null || source is null || snapshot is null
            || document.ClientCaseId != source.CaseId || document.UploadedByUserId != source.ActorId
            || document.UploadedByName != snapshot.ActorName || document.DocumentKey != source.DocumentKey
            || document.OriginalFileName != source.FileName || document.ContentType != source.ContentType
            || document.DocumentType != source.DocumentType || document.Status != source.Status
            || document.SizeBytes != snapshot.ContentSizeBytes || !await sources.CanPublishAsync(source, ct))
            throw new InvalidOperationException("Court publication requires its exact source, original bytes and transaction.");
        // The managed writer invokes this callback before handing ownership to document.Id.
        // Bind the source digest to the actual immutable catalog generation, not a caller's metadata.
        var key = StorageObjectService.GetCommitOperationKey($"case-document:{source.CaseId}:{source.ActorId}:{source.UploadKey:N}");
        var row = await db.StorageOperations.AsNoTracking().Where(o => o.IdempotencyKey == key && o.OperationType == "commit"
                && o.Status == StorageOperationStatuses.Succeeded && o.StorageObject.DataClass == "case-document"
                && o.StorageObject.OwnerType == "case-document" && !o.StorageObject.IsDeleted
                && o.StorageObject.OwnerId == $"generated:case:{source.CaseId}:{source.ActorId}:{source.UploadKey:N}"
                && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
                && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
                && o.StorageObjectGeneration.SizeBytes == snapshot.ContentSizeBytes && o.StorageObjectGeneration.Sha256 == snapshot.ContentSha256)
            .Select(o => o.PayloadJson).SingleOrDefaultAsync(ct);
        var intent = ManagedCaseDocumentUploadService.ParseIntent(row);
        if (!SupportsIntent(intent) || intent!.Generation != generation || intent.DocumentId.HasValue)
            throw new InvalidOperationException("Court publication requires its exact durable content receipt.");
        document.SourceType = "sudrf";
        document.SourceUrl = snapshot.SourceUrl;
        document.SourceCheckedAt = snapshot.CheckedAt;
        document.SourceConfidence = "official-source";
        var clientCase = await db.ClientCases.SingleAsync(c => c.Id == source.CaseId, ct);
        await records.PersistGeneratedCourtAsync(clientCase, document, snapshot, ct);
    }
}
