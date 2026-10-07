using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

public sealed record CaseDocumentGenerationState(string Kind, string RequestSha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TemplateKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DocumentType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Status, bool SourceVerified,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? FinancialKind = null);

public sealed record CaseDocumentUploadState(Guid UploadKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FileName, string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DocumentId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Version,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Sha256,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ContentType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DocumentKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RequestedDocumentKey,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DocumentType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? RequestedStatus, string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CaseDocumentGenerationState? Generation = null);

public interface ICaseDocumentUploadStatusReader
{
    // Exact known attempts only; null means that live publication access was lost.
    Task<IReadOnlyList<CaseDocumentUploadState>?> ReadAsync(int caseId, int actorId,
        IReadOnlyCollection<Guid> uploadKeys, CancellationToken ct);
    Task<CaseDocumentUploadPage?> ListPageAsync(int caseId, int actorId, CancellationToken ct, string? after = null);
}

public sealed record CaseDocumentUploadPage(IReadOnlyList<CaseDocumentUploadState> Items,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor);

public sealed class CaseDocumentUploadStatusReader(PaymentContext db,
    IOptionsMonitor<StorageOrchestrationOptions> options,
    StorageWorkerAvailability? workerAvailability = null) : ICaseDocumentUploadStatusReader
{
    public async Task<CaseDocumentUploadPage?> ListPageAsync(int caseId, int actorId, CancellationToken ct, string? after = null)
    {
        var prefix = $"write:case-document:{caseId}:{actorId}:";
        var keyLength = prefix.Length + 32;
        string? pivot = null;
        if (after is not null)
        {
            if (after.Length is < 1 or > 240) throw new ArgumentException("Invalid cursor.", nameof(after));
            try { pivot = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(after)); }
            catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
            { throw new ArgumentException("Invalid cursor.", nameof(after)); }
            if (pivot.EnumerateRunes().Count() != keyLength || !pivot.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("Invalid cursor.", nameof(after));
        }
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return null;
        var ownerPrefix = $"upload:case:{caseId}:{actorId}:";
        var generatedOwnerPrefix = $"generated:case:{caseId}:{actorId}:";
        var commits = db.StorageOperations.AsNoTracking()
            .Where(o => o.OperationType == "commit" && o.IdempotencyKey.StartsWith(prefix)
                && o.IdempotencyKey.Length == keyLength + 7 && o.IdempotencyKey.EndsWith(":commit")
                && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
                && !o.StorageObject.IsDeleted && (o.StorageObject.OwnerId.StartsWith(ownerPrefix)
                    || o.StorageObject.OwnerId.StartsWith(generatedOwnerPrefix)))
            .Select(o => o.IdempotencyKey.Substring(0, keyLength));
        var localResource = StorageCapacityAdmission.ResourceKey("local", null);
        var reserves = db.StorageCapacityReservations.AsNoTracking()
            .Where(r => r.ResourceKey == localResource && r.Status == StorageCapacityReservationStatuses.Pending
                && r.ProductContextJson != null && r.IdempotencyKey.StartsWith(prefix)
                && r.IdempotencyKey.Length == keyLength + 9 && r.IdempotencyKey.EndsWith(":capacity"))
            .Select(r => r.IdempotencyKey.Substring(0, keyLength));
        // UNION deduplicates a primary reservation and its catalog commit in SQL.
        // Page the raw keys, so corrupt payloads cannot stall discovery or expand memory use.
        var candidates = commits.Union(reserves);
        if (pivot is not null) candidates = candidates.Where(k => k.CompareTo(pivot) > 0);
        var storageWindow = await candidates.TagWith("case-upload-discovery-window").OrderBy(k => k).Take(51).ToArrayAsync(ct);
        // A capture can commit before capacity admission creates a file intent.
        // Seek the indexed metadata separately, then merge two bounded windows.
        var financialCandidates = db.GeneratedFinancialDocumentAttempts.AsNoTracking()
            .Where(a => a.ClientCaseId == caseId && a.ActorId == actorId && a.PublishedDocumentId == null);
        if (pivot is not null)
        {
            var keyPivot = pivot[prefix.Length..];
            financialCandidates = financialCandidates.Where(a => a.UploadKeyText.CompareTo(keyPivot) > 0);
        }
        var financialWindow = await financialCandidates.TagWith("case-upload-discovery-financial-window")
            .OrderBy(a => a.UploadKeyText).Select(a => a.UploadKeyText).Take(51).ToArrayAsync(ct);
        var window = storageWindow.Concat(financialWindow.Select(k => prefix + k))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(51).ToArray();
        var nextCursor = window.Length > 50 ? WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(window[49])) : null;
        var keys = window.Take(50).Select(k => Guid.TryParseExact(k[prefix.Length..], "N", out var key) ? key : Guid.Empty)
            .Where(k => k != Guid.Empty).Distinct().ToArray();
        var states = keys.Length == 0 ? [] : await ReadAsync(caseId, actorId, keys, ct);
        if (states is null || !await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return null;
        return new CaseDocumentUploadPage(states.Where(s => s.Status != "unknown").ToArray(), nextCursor);
    }

    public async Task<IReadOnlyList<CaseDocumentUploadState>?> ReadAsync(int caseId, int actorId,
        IReadOnlyCollection<Guid> uploadKeys, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(uploadKeys);
        if (uploadKeys.Count is < 1 or > 50 || uploadKeys.Contains(Guid.Empty))
            throw new ArgumentException("Invalid upload keys.", nameof(uploadKeys));
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return null;
        var keys = uploadKeys.Distinct().ToArray();
        var operationKeys = keys.ToDictionary(k => k, k => StorageObjectService.GetCommitOperationKey(Key(k)));
        var capacityKeys = keys.ToDictionary(k => k, k => StorageObjectService.GetCapacityReservationKey(Key(k)));
        var exactCapacityKeys = capacityKeys.Values.ToArray();
        var localResource = StorageCapacityAdmission.ResourceKey("local", null);
        // Read the reservation before catalog commits: concurrent publication may
        // release it, but the following commit read still sees its durable receipt.
        var reservations = await db.StorageCapacityReservations.AsNoTracking().TagWith("case-upload-status-reservations")
            .Where(r => exactCapacityKeys.Contains(r.IdempotencyKey) && r.ResourceKey == localResource)
            .Select(r => new { r.IdempotencyKey, r.ProductContextJson, r.Bytes, r.ObjectId, r.BindingHash, r.Status })
            .ToDictionaryAsync(r => r.IdempotencyKey, ct);
        var exactOperationKeys = operationKeys.Values.ToArray();
        var commitQuery = db.StorageOperations.AsNoTracking().TagWith("case-upload-status-commits")
            .Where(o => exactOperationKeys.Contains(o.IdempotencyKey) && o.OperationType == "commit")
            .Select(o => new
            {
                o.Id,
                o.StorageObjectGenerationId,
                o.IdempotencyKey,
                o.PayloadJson,
                o.StorageObjectId,
                o.StorageObject.DataClass,
                o.StorageObject.OwnerType,
                o.StorageObject.OwnerId,
                o.StorageObject.IsDeleted,
                o.StorageObject.LogicalName,
                o.StorageObject.ContentType,
                IsCurrent = o.StorageObjectGeneration != null && o.StorageObjectGeneration.StorageObjectId == o.StorageObjectId
                    && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration,
                Size = o.StorageObjectGeneration == null ? (long?)null : o.StorageObjectGeneration.SizeBytes,
                Sha = o.StorageObjectGeneration == null ? null : o.StorageObjectGeneration.Sha256
            });
        var commits = await commitQuery.ToDictionaryAsync(o => o.IdempotencyKey, ct);
        var intents = keys.ToDictionary(k => k, k => ManagedCaseDocumentUploadService.ParseIntent(
            commits.TryGetValue(operationKeys[k], out var commit) ? commit.PayloadJson
                : reservations.GetValueOrDefault(capacityKeys[k])?.ProductContextJson));
        var publicationKeys = keys.Where(k => intents[k]?.DocumentId is null && commits.ContainsKey(operationKeys[k]))
            .Select(k => CaseDocumentPublicationProcessor.Key(commits[operationKeys[k]].Id)).ToArray();
        // Only pending catalog attempts need queue metadata; one bounded query, no provider I/O.
        var publications = publicationKeys.Length == 0 ? new Dictionary<string, PublicationRow>()
            : await db.StorageOperations.AsNoTracking().TagWith("case-upload-status-publications")
                .Where(o => o.OperationType == StorageOperationTypes.PublishCaseDocument && publicationKeys.Contains(o.IdempotencyKey))
                .Select(o => new PublicationRow(o.IdempotencyKey, o.StorageObjectId, o.StorageObjectGenerationId, o.Status))
                .ToDictionaryAsync(o => o.Key, ct);
        // Publication may finish after the first commit read. Refresh only terminal
        // pending receipts, once, before treating a missing document as a failure.
        var completedKeys = commits.Values.Where(c => publications.TryGetValue(CaseDocumentPublicationProcessor.Key(c.Id), out var q)
                && q.ObjectId == c.StorageObjectId && q.GenerationId == c.StorageObjectGenerationId
                && q.Status is StorageOperationStatuses.Succeeded or StorageOperationStatuses.DeadLetter)
            .Select(c => c.IdempotencyKey).ToArray();
        if (completedKeys.Length > 0)
        {
            var refreshed = await commitQuery.Where(c => completedKeys.Contains(c.IdempotencyKey))
                .ToDictionaryAsync(c => c.IdempotencyKey, ct);
            foreach (var key in completedKeys)
            {
                commits.Remove(key);
                if (refreshed.TryGetValue(key, out var current)) commits.Add(key, current);
            }
            intents = keys.ToDictionary(k => k, k => ManagedCaseDocumentUploadService.ParseIntent(
                commits.TryGetValue(operationKeys[k], out var commit) ? commit.PayloadJson
                    : reservations.GetValueOrDefault(capacityKeys[k])?.ProductContextJson));
        }
        var documentIds = intents.Values.Where(i => i?.DocumentId is > 0).Select(i => i!.DocumentId!.Value).Distinct().ToArray();
        // Bound all domain/binding checks to the requested IDs in one batch, never one query per file.
        var receipts = documentIds.Length == 0 ? new Dictionary<int, ReceiptRow>()
            : await db.ClientCaseDocuments.AsNoTracking().TagWith("case-upload-status-receipts")
            .Where(d => documentIds.Contains(d.Id) && d.ClientCaseId == caseId && d.UploadedByUserId == actorId)
            .Join(db.StorageProductBindings.Where(b => b.Source == LegacyStorageSources.CaseDocuments),
                d => (long)d.Id, b => b.SourceId, (d, b) => new ReceiptRow(
                    d.Id, d.DocumentKey, d.Version, d.OriginalFileName, d.ContentType, d.SizeBytes, b.StorageObjectId))
            .ToDictionaryAsync(d => d.Id, ct);
        // The immutable fence also proves retirement after the intent/catalog is
        // cleaned up. Read exact owned keys in one bounded indexed query.
        var retired = (await db.CaseDocumentUploadRetirements.AsNoTracking().TagWith("case-upload-status-retirements")
            .Where(item => item.CaseId == caseId && item.ActorId == actorId && keys.Contains(item.UploadKey))
            .Select(item => item.UploadKey).ToArrayAsync(ct)).ToHashSet();
        var financialKeys = keys.Where(k => intents[k]?.Generation?.Kind == "financial"
            || !commits.ContainsKey(operationKeys[k]) && !reservations.ContainsKey(capacityKeys[k])).ToArray();
        var financialProofs = financialKeys.Length == 0 ? new Dictionary<Guid, CaseDocumentFinancialStatusProof>()
            : await CaseDocumentFinancialStatusProof.ReadAsync(db, caseId, actorId, financialKeys, ct);
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return null;
        var active = options.CurrentValue.Enabled && options.CurrentValue.UploadCapacityChecksEnabled
            && options.CurrentValue.ProductWriteEnabledClasses.Contains("case-document", StringComparer.Ordinal);
        var automatic = active && options.CurrentValue.AutoCaseDocumentPublicationEnabled
            && options.CurrentValue.WorkerEnabled && workerAvailability?.IsRegistered == true;
        var states = new List<CaseDocumentUploadState>(keys.Length);
        foreach (var key in keys)
        {
            var intent = intents[key];
            var financialProof = financialProofs.GetValueOrDefault(key);
            var state = State(key, "unknown");
            CaseDocumentGenerationState? acceptedGeneration = null;
            if (!commits.ContainsKey(operationKeys[key]) && !reservations.ContainsKey(capacityKeys[key])
                && financialProof is not null
                && GeneratedFinancialDocumentRegistry.MetadataReceipt(financialProof.Proof, financialProof.RequestSha256) is { } accepted)
            {
                acceptedGeneration = new("financial", accepted.Generation.RequestSha256, null,
                    GeneratedFinancialDocumentRegistry.DocumentType(accepted.Proof.Kind), "Черновик", true, accepted.Proof.Kind);
                var acceptedStatus = financialProof.DocumentId.HasValue ? "failed"
                    : !active || financialProof.RecoveryLastStatus == "forbidden" ? "paused"
                    : financialProof.RecoveryLastStatus is "invalid" or "conflict" ? "failed"
                    : automatic ? financialProof.RecoveryAttemptCount > 0 ? "retrying" : "waiting" : "manualRetry";
                state = State(key, acceptedStatus,
                    GeneratedFinancialDocumentRegistry.FileName(accepted.Proof), acceptedGeneration: acceptedGeneration);
            }
            if (Valid(intent, caseId, actorId, key))
            {
                var generatedPending = intent!.Generation is not null && SupportsGeneratedSource(intent);
                if (commits.TryGetValue(operationKeys[key], out var commit))
                {
                    var expectedOwner = intent!.DocumentId?.ToString(CultureInfo.InvariantCulture)
                        ?? $"{(generatedPending ? "generated" : "upload")}:case:{caseId}:{actorId}:{key:N}";
                    if (commit.DataClass == "case-document" && commit.OwnerType == "case-document"
                        && commit.OwnerId == expectedOwner && commit.LogicalName == intent.FileName && commit.ContentType == intent.ContentType
                        && commit.IsCurrent && commit.Size == intent.SizeBytes && commit.Sha == intent.Sha256
                        && (intent.DocumentId.HasValue || intent.Generation is null || generatedPending))
                    {
                        if (commit.IsDeleted) state = State(key, "failed");
                        else if (intent.DocumentId.HasValue)
                        {
                            if (receipts.TryGetValue(intent.DocumentId.Value, out var receipt)
                                && receipt.StorageObjectId == commit.StorageObjectId && receipt.DocumentKey == intent.DocumentKey
                                && receipt.Version == intent.Version && receipt.OriginalFileName == intent.FileName
                                && receipt.ContentType == intent.ContentType && receipt.SizeBytes == intent.SizeBytes
                                && (intent.Generation?.Kind != "financial" || financialProof is { HasWinner: true }
                                    && financialProof.DocumentId == receipt.Id && financialProof.Matches(intent)))
                                state = State(key, "uploaded", intent.FileName, receipt.Id, receipt.Version, intent.Sha256, intent,
                                    financialProof: financialProof);
                            else state = State(key, "failed");
                        }
                        else
                        {
                            var queue = publications.GetValueOrDefault(CaseDocumentPublicationProcessor.Key(commit.Id));
                            var queueStatus = queue?.ObjectId == commit.StorageObjectId
                                && queue.GenerationId == commit.StorageObjectGenerationId ? queue.Status : null;
                            var status = queueStatus is StorageOperationStatuses.DeadLetter or StorageOperationStatuses.Succeeded ? "failed"
                                : queueStatus is null ? active ? "manualRetry" : "paused"
                                : !automatic ? "paused"
                                : queueStatus == StorageOperationStatuses.Failed ? "retrying"
                                : queueStatus is StorageOperationStatuses.Pending or StorageOperationStatuses.Running ? "waiting" : "failed";
                            state = State(key, status, intent.FileName, generationSource: intent, financialProof: financialProof);
                        }
                    }
                }
                else if (reservations.TryGetValue(capacityKeys[key], out var reserve) && !intent!.DocumentId.HasValue
                    && reserve.Status == StorageCapacityReservationStatuses.Pending
                    && (intent.Generation is null || generatedPending)
                    && reserve.ObjectId != Guid.Empty && reserve.Bytes == intent.SizeBytes
                    && reserve.BindingHash is { Length: 64 } && reserve.BindingHash.All(Uri.IsHexDigit))
                    state = State(key, active ? "manualRetry" : "paused", intent.FileName, generationSource: intent, financialProof: financialProof);
            }
            // A verified published receipt takes precedence. Never infer
            // retirement from an unavailable intent or a failed queue entry.
            if (state.Status != "uploaded" && retired.Contains(key)) state = State(key, "retired", state.FileName,
                generationSource: Valid(intent, caseId, actorId, key) ? intent : null, financialProof: financialProof,
                acceptedGeneration: acceptedGeneration);
            states.Add(state);
        }
        return states;
        string Key(Guid key) => $"case-document:{caseId}:{actorId}:{key:N}";
    }

    private sealed record ReceiptRow(int Id, string DocumentKey, int Version, string OriginalFileName,
        string ContentType, long SizeBytes, Guid StorageObjectId);
    private sealed record PublicationRow(string Key, Guid ObjectId, Guid? GenerationId, string Status);

    private static bool Valid(ManagedCaseDocumentUploadService.UploadIntent? intent, int caseId, int actorId, Guid key)
        => ManagedCaseDocumentUploadService.IsValidResumeIntent(intent)
            && intent!.CaseId == caseId && intent.ActorId == actorId && intent.UploadKey == key
            && !intent.FileName.Any(char.IsControl);

    private static bool SupportsGeneratedSource(ManagedCaseDocumentUploadService.UploadIntent intent)
        => GeneratedTemplateDocumentPublication.SupportsIntent(intent)
            || GeneratedWorkOrderDocumentPublication.SupportsIntent(intent)
            || GeneratedCourtDocumentPublication.SupportsIntent(intent)
            || GeneratedFinancialDocumentPublication.SupportsIntent(intent);

    private static CaseDocumentGenerationState? GenerationProof(ManagedCaseDocumentUploadService.UploadIntent? intent,
        CaseDocumentFinancialStatusProof? financialProof)
    {
        if (intent?.Generation is not { } generation) return null;
        if (GeneratedTemplateDocumentPublication.SupportsIntent(intent)
            && GeneratedTemplateDocumentPublication.ReadSnapshot(generation) is { } template)
            return new(generation.Kind, generation.RequestSha256, template.RequestedTemplateKey,
                template.RequestedDocumentType, template.DocumentStatus, true);
        if (GeneratedWorkOrderDocumentPublication.SupportsIntent(intent)
            && GeneratedWorkOrderDocumentPublication.ReadSnapshot(generation) is { } order)
            return new(generation.Kind, generation.RequestSha256, null, null, order.DocumentStatus, true);
        if (GeneratedCourtDocumentPublication.SupportsIntent(intent))
            return new(generation.Kind, generation.RequestSha256, null, null, "received", true);
        if (GeneratedFinancialDocumentPublication.SupportsIntent(intent)
            && GeneratedFinancialDocumentRegistry.ReadProof(generation) is { } financial)
            return new(generation.Kind, generation.RequestSha256, null,
                GeneratedFinancialDocumentRegistry.DocumentType(financial.Kind), "Черновик",
                financialProof?.Matches(intent) == true, financial.Kind);
        // Preserve the existing generic published receipt, but do not let it
        // acknowledge a source-specific generation or masquerade as an upload.
        return new(generation.Kind, generation.RequestSha256, null, null, null, false);
    }

    private static CaseDocumentUploadState State(Guid key, string status, string? name = null, int? documentId = null, int? version = null, string? sha = null,
        ManagedCaseDocumentUploadService.UploadIntent? receipt = null, ManagedCaseDocumentUploadService.UploadIntent? generationSource = null,
        CaseDocumentFinancialStatusProof? financialProof = null, CaseDocumentGenerationState? acceptedGeneration = null)
    {
        var generation = acceptedGeneration ?? GenerationProof(generationSource ?? receipt, financialProof);
        var generated = generation is not null;
        var message = status switch
        {
            "uploaded" => "Документ добавлен в дело.",
            "retired" => generated
                ? "Прежняя попытка формирования прекращена и больше не добавит документ. Новый документ сформируйте отдельным действием."
                : "Прежняя попытка прекращена и больше не добавит документ. Новый файл отправляется отдельным действием.",
            "waiting" => generated
                ? "Документ ещё не добавлен. CRM завершит формирование после подготовки обязательных копий; запускать его повторно не нужно."
                : "Документ ещё не добавлен. CRM завершит загрузку после подготовки обязательных копий; отправлять файл заново не нужно.",
            "retrying" => generated
                ? "Не удалось завершить формирование. CRM повторит попытку автоматически; запускать её повторно не нужно."
                : "Не удалось завершить загрузку. CRM повторит попытку автоматически; отправлять файл заново не нужно.",
            "manualRetry" => generated
                ? "Документ ещё не добавлен. Автоматическое завершение не назначено; повторите формирование с теми же параметрами позже."
                : "Документ ещё не добавлен. Автоматическое завершение не назначено; повторите отправку того же файла позже.",
            "paused" => generated
                ? "Проверенное формирование приостановлено. Документ ещё не добавлен; уточните состояние хранилища у администратора."
                : "Проверенная загрузка приостановлена. Документ ещё не добавлен; уточните состояние хранилища у администратора.",
            "failed" => generated
                ? "Сохранённый результат формирования больше не доступен. Обновите карточку перед новой попыткой."
                : "Сохранённый результат загрузки больше не доступен. Обновите карточку перед новой загрузкой.",
            _ => "Результат этой попытки не подтверждён. Не меняйте файл или данные попытки; повторите проверку позже."
        };
        return new(key, name, status, documentId, version, sha, receipt?.ContentType, receipt?.DocumentKey, receipt?.RequestedDocumentKey,
            receipt?.DocumentType, receipt?.Status, message, generation);
    }
}
