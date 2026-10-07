using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Documents;

namespace PayPlanner.Api.Services.Storage;

public sealed record ManagedCaseDocumentUploadRequest(int CaseId, int ActorId, string ActorName, Guid UploadKey,
    IFormFile File, string FileName, string DocumentType, string Status, string? DocumentKey);
public sealed record ManagedCaseDocumentUploadResult(string Status, int? DocumentId = null, bool IsReplay = false);
// Supplied by a server writer, never bound from the public multipart upload API.
public sealed record ManagedCaseDocumentGeneration(string Kind, string RequestSha256, string FrozenDomainJson);
// Immutable server intent metadata for source-specific resume authorization.
// Never bind this record from an HTTP request.
public sealed record ManagedCaseDocumentGenerationSource(int CaseId, int ActorId, Guid UploadKey,
    string DocumentKey, string? RequestedDocumentKey, string FileName, string ContentType,
    string DocumentType, string Status, ManagedCaseDocumentGeneration Generation);
public delegate Task<ManagedCaseDocumentUploadResult> ManagedCaseDocumentPublication(string relativePath,
    Func<Task<ManagedCaseDocumentUploadResult>> publish, CancellationToken ct);

public interface IManagedCaseDocumentUploadService
{
    bool Enabled { get; }
    Task<ManagedCaseDocumentUploadResult> GenerateAsync(ManagedCaseDocumentUploadRequest request,
        ManagedCaseDocumentGeneration generation, Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct,
        Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>>? authorizeSource = null);
    Task<ManagedCaseDocumentUploadResult?> TryResumeGeneratedAsync(int caseId, int actorId, Guid uploadKey,
        string kind, string requestSha256, Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct,
        Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>>? authorizeSource = null);
    Task<ManagedCaseDocumentUploadResult> ResumeAsync(Guid operationId,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct);
    Task<ManagedCaseDocumentUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct);
    // The controller supplies its existing atomic helper and history/notification writer.
    // Both callbacks must use this scoped context; publication exceptions must roll back.
    Task<ManagedCaseDocumentUploadResult> UploadAsync(ManagedCaseDocumentUploadRequest request,
        Func<ClientCaseDocument, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct);
}

public sealed class ManagedCaseDocumentUploadService(PaymentContext db, IStorageObjectService storage,
    IProductStorageBindingService publisher, IWebHostEnvironment environment, IConfiguration configuration,
    IOptionsMonitor<StorageOrchestrationOptions> options, IStorageStageCapacityService stageCapacity)
    : IManagedCaseDocumentUploadService
{
    internal const int MaxSize = 25 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public bool Enabled => options.CurrentValue.Enabled
        && options.CurrentValue.ProductWriteEnabledClasses.Contains("case-document", StringComparer.Ordinal);

    // Service-level entry point for a durable consumer; neither an HTTP identity nor browser bytes are required.
    public async Task<ManagedCaseDocumentUploadResult> ResumeAsync(Guid operationId,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorizePublication);
        ArgumentNullException.ThrowIfNull(persistDocument);
        ArgumentNullException.ThrowIfNull(atomicPublication);
        if (!Enabled) return new("disabled");
        if (!options.CurrentValue.UploadCapacityChecksEnabled) return new("unavailable");
        var operation = await FindResumeIntentAsync(operationId, ct);
        var intent = ParseIntent(operation?.PayloadJson);
        if (operation is null || intent is null || !IsValidResumeIntent(intent) || intent.Generation is not null) return new("invalid");
        if (!ResumeOperationMatchesIntent(operation, intent)) return new("conflict");
        if (!await CanPublishAsync(intent.ActorId, intent.CaseId, ct)
            || !await authorizePublication(intent.ActorId, intent.CaseId, ct)) return new("forbidden");
        if (intent.DocumentId.HasValue)
        {
            var receipt = await PublishedResultAsync(intent, operation.StorageObjectId, ct, operation.Generation);
            return await CanPublishAsync(intent.ActorId, intent.CaseId, ct)
                && await authorizePublication(intent.ActorId, intent.CaseId, ct) ? receipt : new("forbidden");
        }
        var owner = $"upload:case:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}";
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, intent.CaseId, intent.ActorId, intent.UploadKey, ct))
            return new("retired");
        if (!await db.StorageObjects.AsNoTracking().AnyAsync(o => o.Id == operation.StorageObjectId && !o.IsDeleted
                && o.DataClass == "case-document" && o.OwnerType == "case-document" && o.OwnerId == owner
                && o.LogicalName == intent.FileName && o.ContentType == intent.ContentType
                && o.CurrentGeneration == operation.Generation, ct))
        {
            var winner = await FindResumeIntentAsync(operationId, ct);
            var published = ParseIntent(winner?.PayloadJson);
            if (winner?.StorageObjectId != operation.StorageObjectId || winner.Generation != operation.Generation
                || published?.DocumentId is null || (published with { DocumentId = null }) != intent) return new("conflict");
            var receipt = await PublishedResultAsync(published, operation.StorageObjectId, ct, operation.Generation);
            return await CanPublishAsync(intent.ActorId, intent.CaseId, ct)
                && await authorizePublication(intent.ActorId, intent.CaseId, ct) ? receipt : new("forbidden");
        }
        await using var read = await storage.OpenReadAsync(operation.StorageObjectId, ct);
        if (read is null) return new("unavailable");
        if (read.ObjectId != operation.StorageObjectId || read.Generation != operation.Generation
            || read.LogicalName != intent.FileName || read.ContentType != intent.ContentType
            || read.SizeBytes != intent.SizeBytes || !string.Equals(read.Sha256, intent.Sha256, StringComparison.OrdinalIgnoreCase))
            return new("conflict");
        if (!await CanPublishAsync(intent.ActorId, intent.CaseId, ct)
            || !await authorizePublication(intent.ActorId, intent.CaseId, ct)) return new("forbidden");
        if (!await ResumeIntentIsCurrentAsync(operationId, operation, intent, ct)) return new("conflict");
        // Reuse the ordinary bounded writer, required-copy gate, compatibility file, locks and exact intent receipt.
        return await UploadAsync(new(intent.CaseId, intent.ActorId, intent.ActorName, intent.UploadKey,
            new FormFile(read.Content, 0, intent.SizeBytes, "file", intent.FileName)
            { Headers = new HeaderDictionary(), ContentType = intent.ContentType },
            intent.FileName, intent.DocumentType, intent.Status, intent.RequestedDocumentKey), async (document, token) =>
            {
                // Invoked under the ordinary writer's publication lock, before any domain records are saved.
                if (!await authorizePublication(intent.ActorId, intent.CaseId, token)) throw new PublicationFailure("forbidden");
                if (!await ResumeIntentIsCurrentAsync(operationId, operation, intent, token)) throw new PublicationFailure("conflict");
                await persistDocument(document, token);
            }, atomicPublication, ct);
    }

    // Internal receipt proof for a consumer after a racing publisher wins; never expose it as an unauthorised HTTP read.
    public async Task<ManagedCaseDocumentUploadResult> VerifyPublishedAsync(Guid operationId, CancellationToken ct)
    {
        if (!Enabled) return new("disabled");
        var operation = await FindResumeIntentAsync(operationId, ct);
        var intent = ParseIntent(operation?.PayloadJson);
        if (operation is null || intent is null || !IsValidResumeIntent(intent)) return new("invalid");
        if (!ResumeOperationMatchesIntent(operation, intent)) return new("conflict");
        return intent.DocumentId.HasValue ? await PublishedResultAsync(intent, operation.StorageObjectId, ct, operation.Generation) : new("pending");
    }

    private async Task<bool> ResumeIntentIsCurrentAsync(Guid id, ResumeIntentRow original, UploadIntent intent, CancellationToken ct)
    {
        var current = await FindResumeIntentAsync(id, ct);
        var value = ParseIntent(current?.PayloadJson);
        return current?.StorageObjectId == original.StorageObjectId && current.Generation == original.Generation
            && current.SizeBytes == original.SizeBytes && current.Sha256 == original.Sha256
            && current.IdempotencyKey == original.IdempotencyKey && value is not null
            && (value with { DocumentId = null }) == intent;
    }

    private Task<ResumeIntentRow?> FindResumeIntentAsync(Guid id, CancellationToken ct) => db.StorageOperations.AsNoTracking()
        .Where(o => o.Id == id && o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
            && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId)
        .Select(o => new ResumeIntentRow(o.StorageObjectId, o.IdempotencyKey, o.PayloadJson, o.StorageObjectGeneration!.Generation,
            o.StorageObjectGeneration.SizeBytes, o.StorageObjectGeneration.Sha256))
        .SingleOrDefaultAsync(ct);

    internal static bool IsValidResumeIntent(UploadIntent? i) => i is not null
        && i.CaseId > 0 && i.ActorId > 0 && i.UploadKey != Guid.Empty && i.Version > 0 && i.DocumentId is null or > 0
        && i.SizeBytes > 0 && i.SizeBytes <= MaxSize && i.ActorName is { Length: <= 256 } && i.UploadedAt != default
        && !string.IsNullOrWhiteSpace(i.FileName) && i.FileName.Length <= 260 && !i.FileName.Contains('/') && !i.FileName.Contains('\\')
        && !string.IsNullOrWhiteSpace(i.ContentType) && i.ContentType.Length <= 200
        && !string.IsNullOrWhiteSpace(i.DocumentType) && i.DocumentType.Length <= 120
        && !string.IsNullOrWhiteSpace(i.Status) && i.Status.Length <= 120
        && CaseDocumentKeys.IsValid(i.DocumentKey)
        && (i.RequestedDocumentKey is null || CaseDocumentKeys.IsValid(i.RequestedDocumentKey))
        && (i.RequestedDocumentKey is null || i.RequestedDocumentKey == i.DocumentKey)
        && i.Sha256 is { Length: 64 } && i.Sha256.All(Uri.IsHexDigit)
        && (i.Generation is null || ValidGeneration(i.Generation));

    private static bool ResumeOperationMatchesIntent(ResumeIntentRow operation, UploadIntent intent)
        => operation.IdempotencyKey == StorageObjectService.GetCommitOperationKey($"case-document:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}")
            && operation.SizeBytes == intent.SizeBytes && operation.Sha256 == intent.Sha256;

    private sealed record ResumeIntentRow(Guid StorageObjectId, string IdempotencyKey, string? PayloadJson, long Generation, long SizeBytes, string Sha256);

    public Task<ManagedCaseDocumentUploadResult> UploadAsync(ManagedCaseDocumentUploadRequest request,
        Func<ClientCaseDocument, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct)
        => UploadCoreAsync(request, null, persistDocument, atomicPublication, ct);

    public Task<ManagedCaseDocumentUploadResult> GenerateAsync(ManagedCaseDocumentUploadRequest request,
        ManagedCaseDocumentGeneration generation, Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct,
        Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>>? authorizeSource = null)
    {
        ArgumentNullException.ThrowIfNull(authorizePublication);
        if (authorizeSource is not null)
        {
            if (request.DocumentKey is null) return Task.FromResult(new ManagedCaseDocumentUploadResult("invalid"));
            var source = new ManagedCaseDocumentGenerationSource(request.CaseId, request.ActorId, request.UploadKey,
                request.DocumentKey, request.DocumentKey, request.FileName,
                string.IsNullOrWhiteSpace(request.File.ContentType) ? "application/octet-stream" : request.File.ContentType.Trim(),
                request.DocumentType, request.Status, generation);
            var authorizeCaller = authorizePublication;
            authorizePublication = async (actor, targetCase, token) => actor == source.ActorId && targetCase == source.CaseId
                && await authorizeCaller(actor, targetCase, token) && await authorizeSource(source, token);
        }
        return GenerateCoreAsync(request, generation, authorizePublication, persistDocument, atomicPublication, ct,
            requireSourceFence: authorizeSource is not null);
    }

    private async Task<ManagedCaseDocumentUploadResult> GenerateCoreAsync(ManagedCaseDocumentUploadRequest request,
        ManagedCaseDocumentGeneration generation, Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct, bool requireSourceFence)
    {
        ArgumentNullException.ThrowIfNull(authorizePublication);
        if (!ValidGeneration(generation) || request.DocumentKey is null) return new("invalid");
        if (!await authorizePublication(request.ActorId, request.CaseId, ct)) return new("forbidden");
        if (requireSourceFence)
        {
            var publishAtomically = atomicPublication;
            atomicPublication = (path, publish, token) => publishAtomically(path, async () =>
            {
                var result = await publish();
                // New source callers share this fence for first/recovered writes.
                // Existing consumers retain their own lease/failure protocol.
                if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) throw new PublicationFailure("disabled");
                if (!await CanPublishAsync(request.ActorId, request.CaseId, token)
                    || !await authorizePublication(request.ActorId, request.CaseId, token)) throw new PublicationFailure("forbidden");
                return result;
            }, token);
        }
        return await UploadCoreAsync(request, generation, async (document, token) =>
        {
            if (!await authorizePublication(request.ActorId, request.CaseId, token)) throw new PublicationFailure("forbidden");
            await persistDocument(document, generation, token);
        }, atomicPublication, ct);
    }

    // A null result alone permits rendering new bytes. Every stored attempt,
    // including uncertain provider ACK, prevents silently replacing frozen data.
    public async Task<ManagedCaseDocumentUploadResult?> TryResumeGeneratedAsync(int caseId, int actorId, Guid uploadKey,
        string kind, string requestSha256, Func<int, int, CancellationToken, Task<bool>> authorizePublication,
        Func<ClientCaseDocument, ManagedCaseDocumentGeneration, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct,
        Func<ManagedCaseDocumentGenerationSource, CancellationToken, Task<bool>>? authorizeSource = null)
    {
        ArgumentNullException.ThrowIfNull(authorizePublication);
        if (!Enabled) return new("disabled");
        if (!options.CurrentValue.UploadCapacityChecksEnabled) return new("unavailable");
        if (caseId <= 0 || actorId <= 0 || uploadKey == Guid.Empty || !ValidGeneration(new(kind, requestSha256, "{}")))
            return new("invalid");
        if (!await CanPublishAsync(actorId, caseId, ct) || !await authorizePublication(actorId, caseId, ct)) return new("forbidden");
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, caseId, actorId, uploadKey, ct)) return new("retired");
        var key = $"case-document:{caseId}:{actorId}:{uploadKey:N}";
        var operation = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key), ct);
        var reservationKey = StorageObjectService.GetCapacityReservationKey(key);
        var localResource = StorageCapacityAdmission.ResourceKey("local", null);
        var reserved = operation is null ? await db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.IdempotencyKey == reservationKey && r.ResourceKey == localResource
                && r.Status == StorageCapacityReservationStatuses.Pending)
            .Select(r => new { r.ProductContextJson }).SingleOrDefaultAsync(ct) : null;
        if (operation is null && reserved is null) return null;
        var intent = ParseIntent(operation?.Json ?? reserved?.ProductContextJson);
        if (!IsValidResumeIntent(intent) || intent!.CaseId != caseId || intent.ActorId != actorId || intent.UploadKey != uploadKey
            || intent.Generation?.Kind != kind || intent.Generation.RequestSha256 != requestSha256) return new("conflict");
        if (operation is not null && operation.CommitStatus != StorageOperationStatuses.Succeeded) return new("unavailable");
        if (authorizeSource is not null)
        {
            var source = GenerationSource(intent);
            var authorizeCaller = authorizePublication;
            authorizePublication = async (actor, targetCase, token) => actor == source.ActorId && targetCase == source.CaseId
                && await authorizeCaller(actor, targetCase, token) && await authorizeSource(source, token);
            // The exact stored source is available only after intent validation.
            // Reuse this composed check for reads, locked publication and receipts.
            if (!await authorizePublication(actorId, caseId, ct)) return new("forbidden");
        }
        if (intent.DocumentId.HasValue)
        {
            if (operation is null) return new("conflict");
            return await ConfirmGeneratedWinnerAsync(operation, intent, authorizePublication, ct);
        }
        if (operation is null)
        {
            if (storage is not IStorageReservedReadService recovery) return new("unavailable");
            await using var reservedRead = await recovery.OpenReservedReadAsync(new("case-document", "case-document",
                $"generated:case:{caseId}:{actorId}:{uploadKey:N}", intent.FileName, intent.ContentType, Stream.Null,
                key, actorId, reserved!.ProductContextJson, intent.SizeBytes, intent.Sha256), ct);
            if (reservedRead is null) return new("unavailable");
            // Re-enter the ordinary writer with the frozen bytes; its reservation
            // and HEAD checks confirm the original target without another PUT.
            var recovered = await GenerateCoreAsync(new(caseId, actorId, intent.ActorName, uploadKey,
                new FormFile(reservedRead.Content, 0, intent.SizeBytes, "file", intent.FileName)
                { Headers = new HeaderDictionary(), ContentType = intent.ContentType }, intent.FileName, intent.DocumentType,
                intent.Status, intent.RequestedDocumentKey), intent.Generation, authorizePublication,
                async (document, frozen, token) =>
                {
                    var committed = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key), token);
                    var value = ParseIntent(committed?.Json);
                    if (committed is null || committed.ObjectId != reservedRead.ObjectId
                        || committed.CommitStatus != StorageOperationStatuses.Succeeded || !committed.GenerationBelongsToObject
                        || committed.CommittedGeneration != 1 || value is null
                        || (value with { DocumentId = null }) != intent) throw new PublicationFailure("conflict");
                    await persistDocument(document, frozen, token);
                }, atomicPublication, ct, requireSourceFence: authorizeSource is not null);
            if (recovered.Status is not ("uploaded" or "pending")) return recovered;
            var confirmedCommit = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key), ct);
            var confirmedIntent = ParseIntent(confirmedCommit?.Json);
            if (confirmedCommit is not null && confirmedCommit.CommitStatus != StorageOperationStatuses.Succeeded)
                return new("unavailable");
            if (confirmedCommit is null || confirmedCommit.ObjectId != reservedRead.ObjectId
                || !confirmedCommit.GenerationBelongsToObject || confirmedCommit.CommittedGeneration != 1
                || !IsValidResumeIntent(confirmedIntent) || (confirmedIntent! with { DocumentId = null }) != intent)
                return new("conflict");
            if (recovered.Status == "pending") return recovered;
            var receipt = await ConfirmGeneratedWinnerAsync(confirmedCommit, intent, authorizePublication, ct);
            return receipt.Status == "uploaded" && receipt.DocumentId == recovered.DocumentId
                ? receipt with { IsReplay = recovered.IsReplay } : receipt.Status == "uploaded" ? new("conflict") : receipt;
        }
        if (operation.CommittedGeneration is null || !operation.GenerationBelongsToObject) return new("conflict");
        var pendingOwner = $"generated:case:{caseId}:{actorId}:{uploadKey:N}";
        var proven = await db.StorageOperations.AsNoTracking().TagWith("case-generated-resume-proof")
            .AnyAsync(o => o.Id == operation.OperationId && o.PayloadJson == operation.Json
                && o.Status == StorageOperationStatuses.Succeeded && o.StorageObjectId == operation.ObjectId
                && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
                && o.StorageObjectGeneration.Generation == operation.CommittedGeneration
                && o.StorageObjectGeneration.SizeBytes == intent.SizeBytes && o.StorageObjectGeneration.Sha256 == intent.Sha256
                && !o.StorageObject.IsDeleted && o.StorageObject.DataClass == "case-document"
                && !db.StorageTombstones.Any(t => t.StorageObjectId == o.StorageObjectId)
                && o.StorageObject.OwnerType == "case-document" && o.StorageObject.OwnerId == pendingOwner
                && o.StorageObject.LogicalName == intent.FileName && o.StorageObject.ContentType == intent.ContentType
                && o.StorageObject.CurrentGeneration == operation.CommittedGeneration, ct);
        if (!proven) return await ConfirmGeneratedWinnerAsync(operation, intent, authorizePublication, ct);
        if (!await CanPublishAsync(actorId, caseId, ct) || !await authorizePublication(actorId, caseId, ct)) return new("forbidden");
        await using var read = await storage.OpenReadAsync(operation.ObjectId, ct);
        if (read is null) return new("unavailable");
        if (read.ObjectId != operation.ObjectId || read.Generation != operation.CommittedGeneration
            || read.LogicalName != intent.FileName || read.ContentType != intent.ContentType
            || read.SizeBytes != intent.SizeBytes || read.Sha256 != intent.Sha256) return new("conflict");
        var current = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key), ct);
        var currentIntent = ParseIntent(current?.Json);
        if (!SameGeneratedAttempt(operation, intent, current, currentIntent)) return new("conflict");
        if (currentIntent!.DocumentId.HasValue) return await ConfirmGeneratedWinnerAsync(operation, intent, authorizePublication, ct);
        var result = await GenerateCoreAsync(new(caseId, actorId, intent.ActorName, uploadKey,
            new FormFile(read.Content, 0, intent.SizeBytes, "file", intent.FileName)
            { Headers = new HeaderDictionary(), ContentType = intent.ContentType }, intent.FileName, intent.DocumentType,
            intent.Status, intent.RequestedDocumentKey), intent.Generation, authorizePublication, async (document, frozen, token) =>
            {
                // Do not adopt a changed snapshot between the read and the case lock.
                var locked = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(key), token);
                if (!SameGeneratedAttempt(operation, intent, locked, ParseIntent(locked?.Json))) throw new PublicationFailure("conflict");
                await persistDocument(document, frozen, token);
            }, atomicPublication, ct, requireSourceFence: authorizeSource is not null);
        if (result.Status != "uploaded") return result;
        var confirmed = await ConfirmGeneratedWinnerAsync(operation, intent, authorizePublication, ct);
        return confirmed.Status == "uploaded" && confirmed.DocumentId == result.DocumentId
            ? confirmed with { IsReplay = result.IsReplay } : confirmed.Status == "uploaded" ? new("conflict") : confirmed;
    }

    internal static ManagedCaseDocumentGenerationSource GenerationSource(UploadIntent intent)
        => new(intent.CaseId, intent.ActorId, intent.UploadKey, intent.DocumentKey, intent.RequestedDocumentKey,
            intent.FileName, intent.ContentType, intent.DocumentType, intent.Status,
            intent.Generation ?? throw new InvalidOperationException("A generated source requires its stored generation."));

    private static bool SameGeneratedAttempt(IntentRow original, UploadIntent intent, IntentRow? current, UploadIntent? value)
        => current is not null && current.OperationId == original.OperationId && current.ObjectId == original.ObjectId
            && current.CommitStatus == StorageOperationStatuses.Succeeded && current.CommittedGeneration == original.CommittedGeneration
            && original.GenerationBelongsToObject && current.GenerationBelongsToObject
            && current.CommittedGeneration.HasValue && value is not null && IsValidResumeIntent(value)
            && (value with { DocumentId = null }) == (intent with { DocumentId = null });

    private async Task<ManagedCaseDocumentUploadResult> ConfirmGeneratedWinnerAsync(IntentRow original, UploadIntent intent,
        Func<int, int, CancellationToken, Task<bool>> authorizePublication, CancellationToken ct)
    {
        var current = await FindIntentAsync(StorageObjectService.GetCommitOperationKey(
            $"case-document:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}"), ct);
        var published = ParseIntent(current?.Json);
        if (!SameGeneratedAttempt(original, intent, current, published) || published!.DocumentId is null) return new("conflict");
        var receipt = await PublishedResultAsync(published, current!.ObjectId, ct, current.CommittedGeneration);
        return await CanPublishAsync(intent.ActorId, intent.CaseId, ct)
            && await authorizePublication(intent.ActorId, intent.CaseId, ct) ? receipt : new("forbidden");
    }

    private static bool ValidGeneration(ManagedCaseDocumentGeneration? generation)
    {
        if (generation is null || generation.Kind is not ("template" or "work-order" or "court" or "invoice" or "act" or "contract" or "financial")
            || generation.RequestSha256 is not { Length: 64 } || !generation.RequestSha256.All(Uri.IsHexDigit)
            || generation.FrozenDomainJson is not { Length: > 0 and <= 48000 }) return false;
        try { using var json = JsonDocument.Parse(generation.FrozenDomainJson); return json.RootElement.ValueKind == JsonValueKind.Object; }
        catch (JsonException) { return false; }
    }

    private async Task<ManagedCaseDocumentUploadResult> UploadCoreAsync(ManagedCaseDocumentUploadRequest request,
        ManagedCaseDocumentGeneration? generation,
        Func<ClientCaseDocument, CancellationToken, Task> persistDocument,
        ManagedCaseDocumentPublication atomicPublication, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(persistDocument);
        ArgumentNullException.ThrowIfNull(atomicPublication);
        if (!Enabled) return new("disabled");
        if (!options.CurrentValue.UploadCapacityChecksEnabled) return new("unavailable");
        var contentType = string.IsNullOrWhiteSpace(request.File.ContentType) ? "application/octet-stream" : request.File.ContentType.Trim();
        if (request.CaseId <= 0 || request.ActorId <= 0 || request.UploadKey == Guid.Empty
            || request.File.Length <= 0 || request.File.Length > MaxSize
            || string.IsNullOrWhiteSpace(request.FileName) || request.FileName.Length > 260
            || request.FileName.Contains('/') || request.FileName.Contains('\\')
            || request.ActorName.Length > 256 || contentType.Length > 200
            || string.IsNullOrWhiteSpace(request.DocumentType) || request.DocumentType.Length > 120
            || string.IsNullOrWhiteSpace(request.Status) || request.Status.Length > 120
            || (request.DocumentKey is not null && !CaseDocumentKeys.IsValid(request.DocumentKey)))
            return new("invalid");
        if (!await CanPublishAsync(request.ActorId, request.CaseId, ct)) return new("forbidden");
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, request.CaseId, request.ActorId, request.UploadKey, ct))
            return new("retired");

        var key = $"case-document:{request.CaseId}:{request.ActorId}:{request.UploadKey:N}";
        var operationKey = StorageObjectService.GetCommitOperationKey(key);
        // Generated attempts must not enter the old upload-only history consumer.
        var pendingOwner = $"{(generation is null ? "upload" : "generated")}:case:{request.CaseId}:{request.ActorId}:{request.UploadKey:N}";
        var stageRoot = Path.Combine(LocalStoragePaths.ResolveRoot(environment, configuration), ".case-uploads");
        Directory.CreateDirectory(stageRoot);
        var stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N") + ".tmp");
        StorageStageCapacityTicket? stageTicket = null;
        StorageStageCapacityTicket? compatibilityTicket = null;
        var ownsStage = false;
        try
        {
            var reservation = await stageCapacity.ReserveAsync(key, stage, request.File.Length, ct);
            if (reservation.Ticket is null) return new("unavailable");
            stageTicket = reservation.Ticket;
            string sha;
            await using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                ownsStage = true;
                try { sha = await UploadValidationHelper.CopyValidatedAndHashAsync(request.File, output, MaxSize, ct); }
                catch (InvalidDataException) { return new("invalid"); }
            }
            await stageCapacity.SettlePhysicalAsync(stageTicket, ct);
            var existing = await FindIntentAsync(operationKey, ct);
            UploadIntent? intent;
            if (existing is null)
            {
                var reservedJson = await StorageUploadCapacityReservations.ReadProductContextAsync(db,
                    StorageObjectService.GetCapacityReservationKey(key), ct);
                if (reservedJson is not null)
                {
                    intent = ParseIntent(reservedJson);
                    if (!Matches(intent, request, contentType, sha, generation) || intent!.DocumentId.HasValue) return new("conflict");
                }
                else
                {
                    var documentKey = request.DocumentKey ?? Guid.NewGuid().ToString("N");
                    var latest = await LatestVersionAsync(request.CaseId, documentKey, ct);
                    if (request.DocumentKey is not null && latest == 0 && generation is null) return new("invalid");
                    if (latest == int.MaxValue) return new("conflict");
                    var now = DateTime.UtcNow;
                    // PostgreSQL timestamps persist microseconds. Keep the durable
                    // intent and domain timestamp identical on both providers.
                    var uploadedAt = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
                    intent = new(request.CaseId, request.ActorId, request.ActorName, request.UploadKey,
                        request.DocumentKey, documentKey, latest + 1, request.FileName, contentType,
                        request.DocumentType, request.Status, request.File.Length, sha, uploadedAt, Generation: generation);
                }
            }
            else
            {
                intent = ParseIntent(existing.Json);
                if (!Matches(intent, request, contentType, sha, generation)) return new("conflict");
                if (intent!.DocumentId.HasValue) return await PublishedResultAsync(intent, existing.ObjectId, ct);
            }
            var intentJson = JsonSerializer.Serialize(intent, JsonOptions);
            if (intentJson.Length > 65536) return new("invalid");
            var caseStorage = new LocalCaseDocumentStorage(environment, configuration);
            var extension = Path.GetExtension(intent!.FileName);
            var storedName = $"{intent.DocumentKey}-v{intent.Version}-{request.UploadKey:N}{(extension.Length <= 24 ? extension : "")}";
            var relativePath = caseStorage.BuildRelativePath(request.CaseId, storedName);
            var target = caseStorage.ResolvePath(relativePath);
            if (!File.Exists(target))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = Path.Combine(Path.GetDirectoryName(target)!, Guid.NewGuid().ToString("N") + ".tmp");
                var capacity = await stageCapacity.ReserveCompatibilityAsync(key + ":compat", temporary, request.File.Length, "files", ct);
                if (capacity.Ticket is null) return new("unavailable");
                compatibilityTicket = capacity.Ticket;
            }
            await using var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            StorageObjectWriteResult write;
            try
            {
                write = await storage.WriteAsync(new("case-document", "case-document", pendingOwner,
                    request.FileName, contentType, input, key, request.ActorId, intentJson,
                    request.File.Length, sha), ct);
            }
            catch (InvalidOperationException)
            {
                // Publication may win while this request is staging or confirming provider ACK.
                var winner = await FindIntentAsync(operationKey, ct);
                var published = ParseIntent(winner?.Json);
                if (winner is not null && Matches(published, request, contentType, sha, generation) && published!.DocumentId.HasValue)
                    return await PublishedResultAsync(published, winner.ObjectId, ct);
                if (winner is not null && !Matches(published, request, contentType, sha, generation)) return new("conflict");
                throw;
            }
            if (write.ObjectId == Guid.Empty)
            {
                var winner = await FindIntentAsync(operationKey, ct);
                var published = ParseIntent(winner?.Json);
                if (winner is not null)
                {
                    if (!Matches(published, request, contentType, sha, generation)) return new("conflict");
                    if (published!.DocumentId.HasValue) return await PublishedResultAsync(published, winner.ObjectId, ct);
                }
                var reservedJson = await StorageUploadCapacityReservations.ReadProductContextAsync(db,
                    StorageObjectService.GetCapacityReservationKey(key), ct);
                if (reservedJson is not null && !Matches(ParseIntent(reservedJson), request, contentType, sha, generation)) return new("conflict");
                return new("unavailable");
            }
            existing = await FindIntentAsync(operationKey, ct);
            intent = ParseIntent(existing?.Json);
            if (existing is null || existing.ObjectId != write.ObjectId || !Matches(intent, request, contentType, sha, generation)) return new("conflict");
            if (intent!.DocumentId.HasValue) return await PublishedResultAsync(intent, existing.ObjectId, ct);
            if (write.Status != "available") return new("pending");
            // A racing first writer chooses the durable document key. Use its exact intent.
            extension = Path.GetExtension(intent.FileName);
            storedName = $"{intent.DocumentKey}-v{intent.Version}-{request.UploadKey:N}{(extension.Length <= 24 ? extension : "")}";
            relativePath = caseStorage.BuildRelativePath(request.CaseId, storedName);
            var durableTarget = caseStorage.ResolvePath(relativePath);
            if (!string.Equals(target, durableTarget, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                // A reservation cannot be reused for a different physical target.
                if (compatibilityTicket is not null)
                {
                    await stageCapacity.ReleaseAsync(compatibilityTicket, ct);
                    compatibilityTicket = null;
                }
                if (!File.Exists(durableTarget))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(durableTarget)!);
                    var temporary = Path.Combine(Path.GetDirectoryName(durableTarget)!, Guid.NewGuid().ToString("N") + ".tmp");
                    var capacity = await stageCapacity.ReserveCompatibilityAsync(key + ":compat", temporary, request.File.Length, "files", ct);
                    if (capacity.Ticket is null) return new("unavailable");
                    compatibilityTicket = capacity.Ticket;
                }
            }
            try { await EnsureCompatibilityAsync(stage, durableTarget, intent, compatibilityTicket, ct); }
            catch (InvalidDataException) { return new("conflict"); }
            try
            {
                return await atomicPublication(relativePath, async () =>
                {
                    if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Case publication requires its domain transaction.");
                    if (await db.ClientCases.Where(c => c.Id == request.CaseId)
                            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, c => c.Title), ct) != 1)
                        throw new PublicationFailure("notFound");
                    if (!Enabled || !options.CurrentValue.UploadCapacityChecksEnabled) throw new PublicationFailure("disabled");
                    if (!await CanPublishAsync(request.ActorId, request.CaseId, ct)) throw new PublicationFailure("forbidden");
                    if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, request.CaseId, request.ActorId, request.UploadKey, ct))
                        throw new PublicationFailure("retired");
                    var current = await FindIntentAsync(operationKey, ct);
                    var currentIntent = ParseIntent(current?.Json);
                    if (current is null || current.ObjectId != write.ObjectId || !Matches(currentIntent, request, contentType, sha, generation))
                        throw new PublicationFailure("conflict");
                    if (currentIntent!.DocumentId.HasValue)
                    {
                        var replay = await PublishedResultAsync(currentIntent, current.ObjectId, ct);
                        if (replay.Status != "uploaded") throw new PublicationFailure(replay.Status);
                        return replay;
                    }
                    if (currentIntent != intent || await LatestVersionAsync(request.CaseId, intent.DocumentKey, ct) != intent.Version - 1)
                        throw new PublicationFailure("conflict");
                    var document = new ClientCaseDocument
                    {
                        ClientCaseId = intent.CaseId,
                        DocumentKey = intent.DocumentKey,
                        Version = intent.Version,
                        DocumentType = intent.DocumentType,
                        Status = intent.Status,
                        OriginalFileName = intent.FileName,
                        StoredFileName = storedName,
                        StoragePath = relativePath,
                        ContentType = intent.ContentType,
                        SizeBytes = intent.SizeBytes,
                        UploadedByUserId = intent.ActorId,
                        UploadedByName = intent.ActorName,
                        UploadedAt = intent.UploadedAt,
                    };
                    db.ClientCaseDocuments.Add(document);
                    await persistDocument(document, ct);
                    var owner = document.Id.ToString(CultureInfo.InvariantCulture);
                    var handedOff = await db.StorageObjects.Where(o => o.Id == write.ObjectId && !o.IsDeleted
                            && !db.StorageTombstones.Any(t => t.StorageObjectId == o.Id)
                            && o.DataClass == "case-document" && o.OwnerType == "case-document" && o.OwnerId == pendingOwner
                            && o.LogicalName == intent.FileName && o.ContentType == intent.ContentType && o.Version < long.MaxValue
                            && o.CurrentGeneration == write.Generation
                            && o.Generations.Any(g => g.Generation == o.CurrentGeneration && g.SizeBytes == intent.SizeBytes && g.Sha256 == intent.Sha256))
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.OwnerId, owner).SetProperty(o => o.Version, o => o.Version + 1), ct);
                    if (handedOff != 1) throw new PublicationFailure("conflict");
                    var binding = await publisher.PublishAsync(new(LegacyStorageSources.CaseDocuments, document.Id,
                        write.ObjectId, intent.FileName, intent.ContentType, 0), ct);
                    if (!binding.Published) throw new PublicationFailure(binding.Status == "notReady" ? "pending" : "conflict");
                    var publishedJson = JsonSerializer.Serialize(intent with { DocumentId = document.Id }, JsonOptions);
                    if (await db.StorageOperations.Where(o => o.Id == current.OperationId && o.PayloadJson == current.Json)
                            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PayloadJson, publishedJson), ct) != 1)
                        throw new PublicationFailure("conflict");
                    return new("uploaded", document.Id);
                }, ct);
            }
            catch (PublicationFailure failure) { return new(failure.Status); }
        }
        finally
        {
            try
            {
                if (ownsStage && File.Exists(stage)) File.Delete(stage);
                if (compatibilityTicket is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await stageCapacity.ReleaseAsync(compatibilityTicket, cleanup.Token);
                }
                if (stageTicket is not null)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await stageCapacity.ReleaseAsync(stageTicket, cleanup.Token);
                }
            }
            finally
            {
                if (stageTicket is not null) await stageTicket.CloseWriterAsync();
                if (compatibilityTicket is not null) await compatibilityTicket.CloseWriterAsync();
            }
        }
    }

    private Task<bool> CanPublishAsync(int actorId, int caseId, CancellationToken ct)
        => CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct);

    private async Task<ManagedCaseDocumentUploadResult> PublishedResultAsync(UploadIntent intent, Guid objectId, CancellationToken ct, long? generation = null)
    {
        var owner = intent.DocumentId!.Value.ToString(CultureInfo.InvariantCulture);
        var matches = await db.ClientCaseDocuments.AsNoTracking().AnyAsync(d => d.Id == intent.DocumentId
            && d.ClientCaseId == intent.CaseId && d.DocumentKey == intent.DocumentKey && d.Version == intent.Version
            && d.OriginalFileName == intent.FileName && d.ContentType == intent.ContentType && d.SizeBytes == intent.SizeBytes
            && d.UploadedByUserId == intent.ActorId
            && (!generation.HasValue || (d.UploadedByName == intent.ActorName && d.UploadedAt == intent.UploadedAt)), ct);
        if (!matches) return new("conflict");
        if (!await db.StorageProductBindings.AsNoTracking().AnyAsync(b => b.Source == LegacyStorageSources.CaseDocuments
                && b.SourceId == intent.DocumentId && b.StorageObjectId == objectId
                && !b.StorageObject.IsDeleted && b.StorageObject.DataClass == "case-document" && b.StorageObject.OwnerType == "case-document"
                && !db.StorageTombstones.Any(t => t.StorageObjectId == b.StorageObjectId)
                && b.StorageObject.OwnerId == owner && b.StorageObject.LogicalName == intent.FileName && b.StorageObject.ContentType == intent.ContentType
                && (!generation.HasValue || b.StorageObject.CurrentGeneration == generation.Value)
                && b.StorageObject.Generations.Any(g => g.Generation == b.StorageObject.CurrentGeneration
                    && g.SizeBytes == intent.SizeBytes && g.Sha256 == intent.Sha256), ct)) return new("conflict");
        return new("uploaded", intent.DocumentId, true);
    }

    private async Task<IntentRow?> FindIntentAsync(string key, CancellationToken ct) => await db.StorageOperations.AsNoTracking()
        .Where(o => o.OperationType == "commit" && o.IdempotencyKey == key)
        .Select(o => new IntentRow(o.Id, o.StorageObjectId, o.PayloadJson, o.Status,
            o.StorageObjectGeneration == null ? null : (long?)o.StorageObjectGeneration.Generation,
            o.StorageObjectGeneration != null && o.StorageObjectGeneration.StorageObjectId == o.StorageObjectId)).SingleOrDefaultAsync(ct);
    private Task<int?> LatestVersionCoreAsync(int caseId, string key, CancellationToken ct)
        => db.ClientCaseDocuments.Where(d => d.ClientCaseId == caseId && d.DocumentKey == key).MaxAsync(d => (int?)d.Version, ct);
    private async Task<int> LatestVersionAsync(int caseId, string key, CancellationToken ct) => await LatestVersionCoreAsync(caseId, key, ct) ?? 0;
    internal static UploadIntent? ParseIntent(string? json)
    {
        try { return json is null ? null : JsonSerializer.Deserialize<UploadIntent>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
    private static bool Matches(UploadIntent? intent, ManagedCaseDocumentUploadRequest request, string contentType, string sha,
        ManagedCaseDocumentGeneration? generation = null)
        => intent is not null && intent.CaseId == request.CaseId && intent.ActorId == request.ActorId && intent.UploadKey == request.UploadKey
            && intent.RequestedDocumentKey == request.DocumentKey && intent.FileName == request.FileName && intent.ContentType == contentType
            && intent.DocumentType == request.DocumentType && intent.Status == request.Status && intent.SizeBytes == request.File.Length && intent.Sha256 == sha
            && intent.Version > 0 && intent.DocumentId is null or > 0 && intent.ActorName is { Length: <= 256 }
            && intent.UploadedAt != default && CaseDocumentKeys.IsValid(intent.DocumentKey) && intent.Generation == generation;

    private static async Task EnsureCompatibilityAsync(string stage, string target, UploadIntent intent,
        StorageStageCapacityTicket? ticket, CancellationToken ct)
    {
        async Task VerifyAsync()
        {
            await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (stream.Length != intent.SizeBytes || Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant() != intent.Sha256)
                throw new InvalidDataException("Existing case compatibility copy does not match this upload.");
        }
        if (File.Exists(target)) { await VerifyAsync(); return; }
        if (ticket is null) throw new InvalidOperationException("Case compatibility copy requires capacity reservation.");
        var ownsTemporary = false;
        try
        {
            await ExclusiveUploadFileWriter.WriteNewAsync(ticket.AbsolutePath, async (output, token) =>
            {
                ownsTemporary = true;
                await using var input = new FileStream(stage, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                await input.CopyToAsync(output, token);
            }, ct);
            try { File.Move(ticket.AbsolutePath, target, false); }
            catch (IOException) when (File.Exists(target)) { await VerifyAsync(); }
        }
        finally { if (ownsTemporary && File.Exists(ticket.AbsolutePath)) File.Delete(ticket.AbsolutePath); }
    }

    private sealed class PublicationFailure(string status) : Exception { public string Status => status; }
    private sealed record IntentRow(Guid OperationId, Guid ObjectId, string? Json, string CommitStatus,
        long? CommittedGeneration, bool GenerationBelongsToObject);
    internal sealed record UploadIntent(int CaseId, int ActorId, string ActorName, Guid UploadKey, string? RequestedDocumentKey,
        string DocumentKey, int Version, string FileName, string ContentType, string DocumentType, string Status,
        long SizeBytes, string Sha256, DateTime UploadedAt, int? DocumentId = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ManagedCaseDocumentGeneration? Generation = null);
}
