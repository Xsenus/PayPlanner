using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed class CaseDocumentUploadRetirementService(PaymentContext db)
{
    internal static Task<bool> IsRetiredAsync(PaymentContext context, int caseId, int actorId, Guid uploadKey, CancellationToken ct)
        => context.CaseDocumentUploadRetirements.AsNoTracking()
            .AnyAsync(item => item.CaseId == caseId && item.ActorId == actorId && item.UploadKey == uploadKey, ct);

    // Does not remove documents, objects, replicas, reservations or saved intent.
    // A subsequent new key is a separate, explicit upload by the user.
    public async Task<string> RetireAsync(int caseId, int actorId, Guid uploadKey, CancellationToken ct)
    {
        if (caseId <= 0 || actorId <= 0 || uploadKey == Guid.Empty) return "invalid";
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return "forbidden";
        if (db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Retirement requires its own publication transaction.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // The ordinary writer takes the same case row lock before persisting any
        // domain record. A publisher either wins first or sees this fence.
        if (await db.ClientCases.Where(item => item.Id == caseId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Title, item => item.Title), ct) != 1)
            return "notFound";
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return "forbidden";
        if (await IsRetiredAsync(db, caseId, actorId, uploadKey, ct)) return "retired";
        var key = $"case-document:{caseId}:{actorId}:{uploadKey:N}";
        var commitKey = StorageObjectService.GetCommitOperationKey(key);
        var commit = await db.StorageOperations.AsNoTracking()
            .Where(item => item.IdempotencyKey == commitKey && item.OperationType == "commit")
            .Select(item => new
            {
                item.PayloadJson,
                item.StorageObjectId,
                item.StorageObject.DataClass,
                item.StorageObject.OwnerType,
                item.StorageObject.OwnerId
            }).SingleOrDefaultAsync(ct);
        var intent = ManagedCaseDocumentUploadService.ParseIntent(commit?.PayloadJson);
        var objectId = commit?.StorageObjectId ?? Guid.Empty;
        if (commit is null)
        {
            var capacityKey = StorageObjectService.GetCapacityReservationKey(key);
            var localResource = StorageCapacityAdmission.ResourceKey("local", null);
            var reserve = await db.StorageCapacityReservations.AsNoTracking()
                .Where(item => item.IdempotencyKey == capacityKey && item.ResourceKey == localResource
                    && item.Status == StorageCapacityReservationStatuses.Pending)
                .Select(item => new { item.ProductContextJson, item.ObjectId, item.Bytes, item.BindingHash })
                .SingleOrDefaultAsync(ct);
            intent = ManagedCaseDocumentUploadService.ParseIntent(reserve?.ProductContextJson);
            if (reserve is null)
            {
                if (await db.StorageCapacityReservations.AsNoTracking()
                    .AnyAsync(item => item.IdempotencyKey == capacityKey && item.ResourceKey == localResource, ct))
                    return "unknown";
                var proofs = await CaseDocumentFinancialStatusProof.ReadAsync(db, caseId, actorId, [uploadKey], ct);
                if (!proofs.TryGetValue(uploadKey, out var accepted)
                    || GeneratedFinancialDocumentRegistry.MetadataReceipt(accepted.Proof, accepted.RequestSha256) is null)
                    return "unknown";
                // A historical publication cannot become a new attempt merely
                // because its document or storage intent was later removed.
                if (accepted.DocumentId.HasValue) return "published";
                return await PersistAsync(null);
            }
            if (intent is null || reserve.Bytes != intent.SizeBytes
                || reserve.BindingHash is not { Length: 64 } || !reserve.BindingHash.All(Uri.IsHexDigit)) return "unknown";
            objectId = reserve.ObjectId;
        }
        if (intent is null || !ManagedCaseDocumentUploadService.IsValidResumeIntent(intent)
            || intent.CaseId != caseId || intent.ActorId != actorId || intent.UploadKey != uploadKey
            || objectId == Guid.Empty) return "unknown";
        // A generated reservation or commit must retain its exact, supported
        // server source. Generic JSON validity cannot prove generation origin.
        if (intent.Generation is not null
            && !GeneratedTemplateDocumentPublication.SupportsIntent(intent)
            && !GeneratedWorkOrderDocumentPublication.SupportsIntent(intent)
            && !GeneratedCourtDocumentPublication.SupportsIntent(intent)
            && !GeneratedFinancialDocumentPublication.SupportsIntent(intent)) return "unknown";
        // The caller must refresh and prove the published receipt; retirement
        // never substitutes for that proof and never deletes a published file.
        if (intent.DocumentId.HasValue) return "published";
        if (commit is not null && (commit.DataClass != "case-document" || commit.OwnerType != "case-document"
            || commit.OwnerId != $"{(intent.Generation is null ? "upload" : "generated")}:case:{caseId}:{actorId}:{uploadKey:N}")) return "unknown";
        return await PersistAsync(objectId);

        async Task<string> PersistAsync(Guid? storageObjectId)
        {
            // The model stores PostgreSQL timestamps without a time zone. Preserve
            // UTC clock fields instead of letting a raw timestamptz parameter be
            // converted through the connection's current session time zone.
            var retiredAt = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
            object timestamp = db.Database.IsNpgsql()
                ? new Npgsql.NpgsqlParameter("retired_at", NpgsqlTypes.NpgsqlDbType.Timestamp) { Value = retiredAt }
                : retiredAt;
            object storageParameter = db.Database.IsNpgsql()
                ? new Npgsql.NpgsqlParameter("storage_object", NpgsqlTypes.NpgsqlDbType.Uuid)
                { Value = storageObjectId.HasValue ? storageObjectId.Value : DBNull.Value }
                : new Microsoft.Data.Sqlite.SqliteParameter("storage_object", Microsoft.Data.Sqlite.SqliteType.Text)
                { Value = storageObjectId.HasValue ? storageObjectId.Value : DBNull.Value };
            // Persist just this marker, without flushing unrelated tracked edits.
            // The case lock serializes inserts for this immutable composite key.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "CaseDocumentUploadRetirements" ("CaseId", "ActorId", "UploadKey", "StorageObjectId", "RetiredAt")
            VALUES ({caseId}, {actorId}, {uploadKey}, {storageParameter}, {timestamp})
            """, ct);
            await transaction.CommitAsync(ct);
            return "retired";
        }
    }
}
