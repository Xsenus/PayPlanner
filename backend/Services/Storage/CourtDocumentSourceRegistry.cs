using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.LegalIntegrations;

namespace PayPlanner.Api.Services.Storage;

public sealed record CourtDocumentSourceInput(string DocumentKey, string FileName, string Title, string SourceUrl, string RenderedHtml,
    string? LegacyDocumentKey = null);
public sealed record GeneratedCourtDocumentSnapshot(int Version, int CaseId, Guid SourceId, Guid AttemptId,
    int AttemptVersion, int ActorId, string ActorName, string DocumentKey, string FileName, string Title,
    string BindingUrl, string SourceUrl, DateTime CheckedAt, long ContentSizeBytes, string ContentSha256);
public sealed record CourtDocumentSourceReceipt(GeneratedCourtDocumentSnapshot Snapshot, ManagedCaseDocumentGeneration Generation);
public sealed record CourtDocumentSourceBatchResult(string Status, IReadOnlyList<CourtDocumentSourceReceipt> Sources);

// Called only with accepted server-side court evidence, in a clean unit of work.
// It owns a short metadata transaction and performs no provider/network I/O.
public sealed class CourtDocumentSourceRegistry(PaymentContext db)
{
    internal const string GenerationKind = "court";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CourtDocumentSourceBatchResult> RecordAcceptedBatchAsync(int caseId, int actorId, string actorName,
        string bindingUrl, DateTime checkedAt, IReadOnlyCollection<CourtDocumentSourceInput> inputs, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Court source capture requires a clean, independent unit of work.");
        if (caseId <= 0 || actorId <= 0 || string.IsNullOrWhiteSpace(actorName) || actorName.Length > 256
            || !ValidUrl(bindingUrl) || checkedAt.Kind != DateTimeKind.Utc || checkedAt < DateTime.UnixEpoch
            || inputs.Count > 500 || inputs.Any(i => !ValidInput(i))) return Result("invalid");
        var groups = inputs.GroupBy(i => i.DocumentKey, StringComparer.Ordinal).ToArray();
        if (groups.Any(g => g.Distinct().Take(2).Count() > 1)) return Result("conflict");
        var unique = groups.Select(g => g.First()).OrderBy(i => i.DocumentKey, StringComparer.Ordinal).ToArray();
        var inputSha = InputHash(unique);
        var bindingSha = Hash(bindingUrl);
        // Both providers preserve the same UTC wall clock, including PostgreSQL microseconds.
        checkedAt = new DateTime(checkedAt.Ticks - checkedAt.Ticks % 10, DateTimeKind.Utc);
        if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return Result("forbidden");
        var before = db.ChangeTracker.Entries().Select(e => (e.Entity, Current: e.CurrentValues.Clone(), Original: e.OriginalValues.Clone())).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var commitStarted = false;
        try
        {
            if (await db.ClientCases.Where(c => c.Id == caseId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, c => c.Title), ct) != 1) return Result("notFound");
            var currentBinding = await db.ClientCases.AsNoTracking().Where(c => c.Id == caseId).Select(c => c.CourtUrl).SingleAsync(ct);
            if (currentBinding != bindingUrl || !await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct))
                return Result("forbidden");
            var scope = await db.CourtDocumentSourceScopes.AsNoTracking().SingleOrDefaultAsync(s => s.ClientCaseId == caseId && s.BindingSha256 == bindingSha, ct);
            if (scope is not null)
            {
                scope = TrackFresh(scope);
                if (scope.BindingUrl != bindingUrl) return Result("conflict");
                if (scope.LastAcceptedAt > checkedAt || scope.LastAcceptedAt == checkedAt && scope.InputSha256 != inputSha)
                    return Result("stale");
                if (scope.Version == long.MaxValue) return Result("invalid");
            }
            var scopeIsNew = scope is null;
            scope ??= new CourtDocumentSourceScope { ClientCaseId = caseId, BindingUrl = bindingUrl, BindingSha256 = bindingSha };
            var keys = unique.Select(i => i.DocumentKey).ToArray();
            var sources = await db.CourtDocumentSources.AsNoTracking().Where(s => s.ClientCaseId == caseId
                && (s.ScopeId == scope.Id && s.IsActive || keys.Contains(s.DocumentKey))).ToListAsync(ct);
            sources = sources.Select(TrackFresh).ToList();
            if (sources.Any(s => s.ScopeId != scope.Id)) return Result("conflict");
            var known = sources.ToDictionary(s => s.DocumentKey, StringComparer.Ordinal);
            var sourcesById = sources.ToDictionary(s => s.Id);
            var existingAttempts = await db.CourtDocumentSourceAttempts.AsNoTracking()
                .Where(a => a.Source.ClientCaseId == caseId && keys.Contains(a.Source.DocumentKey)
                    && a.Version == a.Source.CurrentAttemptVersion).ToArrayAsync(ct);
            foreach (var attempt in existingAttempts)
            {
                attempt.Source = sourcesById[attempt.SourceId];
                attempt.Source.Scope = scope;
            }
            var receipts = existingAttempts.Select(TryReceipt).ToArray();
            if (receipts.Any(r => r is null) || receipts.Length != unique.Count(i => known.ContainsKey(i.DocumentKey)))
                return Result("conflict");
            var existingByKey = receipts.ToDictionary(r => r!.Snapshot.DocumentKey, r => r!, StringComparer.Ordinal);
            if (unique.Any(i => existingByKey.TryGetValue(i.DocumentKey, out var r) && r.Snapshot.SourceUrl != i.SourceUrl))
                return Result("conflict");
            if (!scopeIsNew && scope.LastAcceptedAt == checkedAt && scope.InputSha256 == inputSha)
                return unique.All(i => existingByKey.ContainsKey(i.DocumentKey))
                    ? new("recorded", unique.Select(i => existingByKey[i.DocumentKey]).ToArray()) : Result("conflict");

            // A new accepted observation may replace a deleted published summary.
            // Pending, malformed and retired attempts keep their original identity.
            var renewKeys = await FindDeletedPublishedSourcesAsync(existingByKey.Values, unique, ct);
            if (renewKeys.Any(key => known[key].CurrentAttemptVersion == int.MaxValue)) return Result("invalid");
            var returningKeys = known.Values.Where(s => !s.IsActive).Select(s => s.DocumentKey).ToHashSet(StringComparer.Ordinal);

            var version = scopeIsNew ? 1 : scope.Version + 1;
            if (scopeIsNew) db.CourtDocumentSourceScopes.Add(scope);
            scope.Version = version;
            scope.InputSha256 = inputSha;
            scope.LastAcceptedAt = checkedAt;
            scope.LastActorId = actorId;
            scope.LastActorName = actorName;
            foreach (var source in sources) { source.IsActive = false; source.AcceptedScopeVersion = version; }
            var result = new List<CourtDocumentSourceReceipt>(unique.Length);
            var pendingContents = new List<CourtDocumentSourceContent>();
            long pendingContentBytes = 0;
            foreach (var input in unique)
            {
                if (known.TryGetValue(input.DocumentKey, out var existing))
                {
                    existing.IsActive = true;
                    if (!renewKeys.Contains(input.DocumentKey))
                    {
                        result.Add(existingByKey[input.DocumentKey]);
                        continue;
                    }
                    existing.CurrentAttemptVersion++;
                }
                var source = existing ?? new CourtDocumentSource
                {
                    Scope = scope,
                    ScopeId = scope.Id,
                    ClientCaseId = caseId,
                    DocumentKey = input.DocumentKey,
                    AcceptedScopeVersion = version
                };
                var attempt = new CourtDocumentSourceAttempt
                {
                    Source = source,
                    SourceId = source.Id,
                    Version = source.CurrentAttemptVersion,
                    ActorId = actorId,
                    ActorName = actorName
                };
                var bytes = Encoding.UTF8.GetBytes(input.RenderedHtml);
                var contentSha = Hash(bytes);
                var snapshot = new GeneratedCourtDocumentSnapshot(2, caseId, source.Id, attempt.Id, attempt.Version, actorId, actorName,
                    input.DocumentKey, input.FileName, input.Title, bindingUrl, input.SourceUrl, checkedAt, bytes.LongLength, contentSha);
                attempt.FrozenSnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
                attempt.FrozenSnapshotSha256 = Hash(attempt.FrozenSnapshotJson);
                attempt.RequestSha256 = RequestHash(snapshot);
                db.CourtDocumentSourceAttempts.Add(attempt);
                var content = new CourtDocumentSourceContent
                {
                    Attempt = attempt,
                    AttemptId = attempt.Id,
                    Bytes = bytes,
                    SizeBytes = bytes.LongLength,
                    Sha256 = contentSha
                };
                db.CourtDocumentSourceContents.Add(content);
                pendingContents.Add(content);
                pendingContentBytes += bytes.LongLength;
                result.Add(TryReceipt(attempt) ?? throw new InvalidOperationException("Invalid server court source snapshot."));
                // Bound tracked binary buffers without narrowing the accepted event set.
                // Every flush remains inside this same transaction and rolls back together.
                if (pendingContentBytes >= 8 * 1024 * 1024) await FlushContentsAsync();
            }
            await FlushContentsAsync();
            var recoveredActors = await RecoverReturningPublicationsAsync(
                result.Where(r => returningKeys.Contains(r.Snapshot.DocumentKey)).ToArray(), ct);
            foreach (var originalActor in recoveredActors)
                if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, originalActor, caseId, ct))
                    throw new InvalidOperationException("Court queue recovery lost its original author's authority.");
            if (!await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct))
                throw new InvalidOperationException("Court source capture lost its authority.");
            commitStarted = true;
            await transaction.CommitAsync(ct);
            return new("recorded", result);

            async Task FlushContentsAsync()
            {
                await db.SaveChangesAsync(ct);
                foreach (var content in pendingContents) db.Entry(content).State = EntityState.Detached;
                pendingContents.Clear();
                pendingContentBytes = 0;
            }
        }
        catch (Exception original)
        {
            var rolledBack = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await transaction.RollbackAsync(timeout.Token);
                rolledBack = true;
            }
            catch (Exception rollback) { original.Data["CourtSourceRollbackFailure"] = rollback; }
            if (rolledBack || !commitStarted)
            {
                var originals = before.Select(b => b.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var entry in db.ChangeTracker.Entries().ToArray())
                    if (!originals.Contains(entry.Entity)) entry.State = EntityState.Detached;
                foreach (var saved in before)
                {
                    var entry = db.Entry(saved.Entity);
                    entry.CurrentValues.SetValues(saved.Current);
                    entry.OriginalValues.SetValues(saved.Original);
                    entry.State = EntityState.Unchanged;
                }
            }
            throw;
        }
    }

    public async Task<bool> CanPublishAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
    {
        var snapshot = ReadSnapshot(source.Generation);
        if (snapshot is null || source.ActorId != snapshot.ActorId || source.CaseId != snapshot.CaseId
            || source.UploadKey != snapshot.AttemptId || source.DocumentKey != snapshot.DocumentKey
            || source.RequestedDocumentKey != source.DocumentKey || source.FileName != snapshot.FileName
            || source.DocumentType != CourtSyncNormalization.DocumentTypes.CourtEventSummary || source.Status != "received"
            || source.ContentType != "text/html; charset=utf-8"
            || !await CaseDocumentUploadAuthorization.CanPublishAsync(db, source.ActorId, source.CaseId, ct)) return false;
        var attempt = await db.CourtDocumentSourceAttempts.AsNoTracking().Include(a => a.Source).ThenInclude(s => s.Scope)
            .SingleOrDefaultAsync(a => a.Id == snapshot.AttemptId && a.SourceId == snapshot.SourceId
                && a.Source.ClientCaseId == source.CaseId && a.ActorId == source.ActorId
                && a.Source.IsActive && a.Source.AcceptedScopeVersion == a.Source.Scope.Version
                && a.Version == a.Source.CurrentAttemptVersion, ct);
        var receipt = attempt is null ? null : TryReceipt(attempt);
        return receipt is not null && attempt!.FrozenSnapshotSha256 == Hash(source.Generation.FrozenDomainJson)
            && receipt.Generation.RequestSha256 == source.Generation.RequestSha256
            && await db.CourtDocumentSourceContents.AsNoTracking().AnyAsync(c => c.AttemptId == snapshot.AttemptId
                && c.SizeBytes == snapshot.ContentSizeBytes && c.Sha256 == snapshot.ContentSha256, ct)
            && await db.ClientCases.AsNoTracking().AnyAsync(c => c.Id == source.CaseId && c.CourtUrl == snapshot.BindingUrl, ct);
    }

    // Only the producer needs these original bytes; publication fences query metadata, not the BLOB.
    public async Task<byte[]?> ReadContentAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
    {
        if (!await CanPublishAsync(source, ct)) return null;
        var snapshot = ReadSnapshot(source.Generation)!;
        var content = await db.CourtDocumentSourceContents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.AttemptId == snapshot.AttemptId, ct);
        if (content is null || content.SizeBytes != snapshot.ContentSizeBytes || content.Sha256 != snapshot.ContentSha256
            || content.Bytes.LongLength != snapshot.ContentSizeBytes || Hash(content.Bytes) != snapshot.ContentSha256
            || !await CanPublishAsync(source, ct)) return null;
        return content.Bytes;
    }

    internal static GeneratedCourtDocumentSnapshot? ReadSnapshot(ManagedCaseDocumentGeneration generation)
    {
        if (generation.Kind != GenerationKind || generation.FrozenDomainJson is not { Length: > 0 and <= 48000 }) return null;
        try
        {
            var value = JsonSerializer.Deserialize<GeneratedCourtDocumentSnapshot>(generation.FrozenDomainJson, JsonOptions);
            return value is
            {
                Version: 2, CaseId: > 0, AttemptVersion: > 0, ActorId: > 0, ActorName.Length: > 0 and <= 256,
                ContentSizeBytes: > 0 and <= ManagedCaseDocumentUploadService.MaxSize, ContentSha256.Length: 64
            }
                && value.SourceId != Guid.Empty && value.AttemptId != Guid.Empty && value.CheckedAt.Kind == DateTimeKind.Utc
                && value.CheckedAt >= DateTime.UnixEpoch && ValidUrl(value.BindingUrl)
                && ValidMetadata(value.DocumentKey, value.FileName, value.Title, value.SourceUrl)
                && value.ContentSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                && generation.RequestSha256 == RequestHash(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static CourtDocumentSourceReceipt? TryReceipt(CourtDocumentSourceAttempt attempt)
    {
        var generation = new ManagedCaseDocumentGeneration(GenerationKind, attempt.RequestSha256, attempt.FrozenSnapshotJson);
        var value = ReadSnapshot(generation);
        return value is not null && value.AttemptId == attempt.Id && value.SourceId == attempt.SourceId
            && value.CaseId == attempt.Source.ClientCaseId && value.DocumentKey == attempt.Source.DocumentKey
            && value.BindingUrl == attempt.Source.Scope.BindingUrl && value.AttemptVersion == attempt.Version
            && value.ActorId == attempt.ActorId && value.ActorName == attempt.ActorName
            && Hash(attempt.FrozenSnapshotJson) == attempt.FrozenSnapshotSha256 ? new(value, generation) : null;
    }

    private async Task<HashSet<string>> FindDeletedPublishedSourcesAsync(
        ICollection<CourtDocumentSourceReceipt> receipts, IReadOnlyCollection<CourtDocumentSourceInput> inputs, CancellationToken ct)
    {
        if (receipts.Count == 0) return new(StringComparer.Ordinal);
        var byCommit = receipts.ToDictionary(r => StorageObjectService.GetCommitOperationKey(
            $"case-document:{r.Snapshot.CaseId}:{r.Snapshot.ActorId}:{r.Snapshot.AttemptId:N}"), StringComparer.Ordinal);
        var keys = byCommit.Keys.ToArray();
        var commits = await db.StorageOperations.AsNoTracking().Where(o => keys.Contains(o.IdempotencyKey)
            && o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
            && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
            && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
            && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration)
            .Select(o => new
            {
                o.IdempotencyKey,
                o.PayloadJson,
                o.StorageObject.OwnerId,
                o.StorageObjectGeneration!.SizeBytes,
                o.StorageObjectGeneration.Sha256
            }).ToArrayAsync(ct);
        var published = commits.Select(row => (Row: row, Intent: ManagedCaseDocumentUploadService.ParseIntent(row.PayloadJson)))
            .Where(pair => GeneratedCourtDocumentPublication.SupportsIntent(pair.Intent)
                && pair.Intent!.Generation == byCommit[pair.Row.IdempotencyKey].Generation
                && pair.Intent.DocumentId.HasValue
                && pair.Row.OwnerId == pair.Intent.DocumentId.Value.ToString(CultureInfo.InvariantCulture)
                && pair.Row.SizeBytes == byCommit[pair.Row.IdempotencyKey].Snapshot.ContentSizeBytes
                && pair.Row.Sha256 == byCommit[pair.Row.IdempotencyKey].Snapshot.ContentSha256)
            .Select(pair => pair.Intent!).ToArray();
        if (published.Length == 0) return new(StringComparer.Ordinal);
        var documentIds = published.Select(i => i.DocumentId!.Value).ToArray();
        var caseId = receipts.First().Snapshot.CaseId;
        var acceptedKeys = inputs.SelectMany(i => i.LegacyDocumentKey is null
            ? new[] { i.DocumentKey } : new[] { i.DocumentKey, i.LegacyDocumentKey }).Distinct(StringComparer.Ordinal).ToArray();
        // Query fresh surviving versions under the case transaction, including the
        // legacy identities supplied by the accepted server-side court observation.
        var documents = await db.ClientCaseDocuments.AsNoTracking().Where(d => d.ClientCaseId == caseId
            && (documentIds.Contains(d.Id) || acceptedKeys.Contains(d.DocumentKey)))
            .Select(d => new { d.Id, d.DocumentKey }).ToArrayAsync(ct);
        var present = documents.Select(d => d.Id).ToHashSet();
        var presentKeys = documents.Select(d => d.DocumentKey).ToHashSet(StringComparer.Ordinal);
        var legacyByKey = inputs.ToDictionary(i => i.DocumentKey, i => i.LegacyDocumentKey, StringComparer.Ordinal);
        var attemptIds = published.Select(i => i.UploadKey).ToArray();
        var retired = (await db.CaseDocumentUploadRetirements.AsNoTracking()
            .Where(r => r.CaseId == caseId && attemptIds.Contains(r.UploadKey)).Select(r => r.UploadKey).ToArrayAsync(ct)).ToHashSet();
        return published.Where(i => !present.Contains(i.DocumentId!.Value) && !retired.Contains(i.UploadKey)
                && !presentKeys.Contains(i.DocumentKey)
                && !(legacyByKey[i.DocumentKey] is { } legacyKey && presentKeys.Contains(legacyKey)))
            .Select(i => i.DocumentKey).ToHashSet(StringComparer.Ordinal);
    }

    private async Task<int[]> RecoverReturningPublicationsAsync(IReadOnlyCollection<CourtDocumentSourceReceipt> receipts,
        CancellationToken ct)
    {
        if (receipts.Count == 0) return [];
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Court queue recovery requires the accepted-source transaction.");
        var byCommit = receipts.ToDictionary(r => StorageObjectService.GetCommitOperationKey(
            $"case-document:{r.Snapshot.CaseId}:{r.Snapshot.ActorId}:{r.Snapshot.AttemptId:N}"), StringComparer.Ordinal);
        var keys = byCommit.Keys.ToArray();
        var commits = await db.StorageOperations.AsNoTracking().Where(o => keys.Contains(o.IdempotencyKey)
            && o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
            && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
            && !o.StorageObject.IsDeleted && !db.StorageTombstones.Any(t => t.StorageObjectId == o.StorageObjectId)
            && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
            && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration)
            .Select(o => new
            {
                o.Id,
                o.IdempotencyKey,
                o.StorageObjectId,
                o.StorageObjectGenerationId,
                o.PayloadJson,
                o.StorageObject.OwnerId,
                o.StorageObjectGeneration!.SizeBytes,
                o.StorageObjectGeneration.Sha256
            }).ToArrayAsync(ct);
        var valid = commits.Where(c => GeneratedCourtDocumentPublication.CanSchedule(c.PayloadJson, c.OwnerId)
                && ManagedCaseDocumentUploadService.ParseIntent(c.PayloadJson)!.Generation == byCommit[c.IdempotencyKey].Generation
                && c.SizeBytes == byCommit[c.IdempotencyKey].Snapshot.ContentSizeBytes
                && c.Sha256 == byCommit[c.IdempotencyKey].Snapshot.ContentSha256)
            .ToDictionary(c => CaseDocumentPublicationProcessor.Key(c.Id), StringComparer.Ordinal);
        if (valid.Count == 0) return [];
        var publicationKeys = valid.Keys.ToArray();
        var queued = await db.StorageOperations.AsNoTracking().Where(o => publicationKeys.Contains(o.IdempotencyKey)
            && o.OperationType == StorageOperationTypes.PublishCaseDocument && o.Status == StorageOperationStatuses.DeadLetter
            && o.LastError == "Case publication: forbidden." && o.CompletedAt != null
            && o.LeaseOwner == null && o.LeaseExpiresAt == null).ToArrayAsync(ct);
        var candidates = queued.Where(q => q.StorageObjectId == valid[q.IdempotencyKey].StorageObjectId
                && q.StorageObjectGenerationId == valid[q.IdempotencyKey].StorageObjectGenerationId
                && CaseDocumentPublicationProcessor.ReferencesCommit(q.PayloadJson, valid[q.IdempotencyKey].Id))
            .Select(q => (Operation: q, Receipt: byCommit[valid[q.IdempotencyKey].IdempotencyKey])).ToArray();
        if (candidates.Length == 0) return [];
        var caseId = receipts.First().Snapshot.CaseId;
        var attemptIds = candidates.Select(c => c.Receipt.Snapshot.AttemptId).ToArray();
        // Batch source/content metadata; never load original HTML or contact providers.
        var metadata = await db.CourtDocumentSourceContents.AsNoTracking().Where(c => attemptIds.Contains(c.AttemptId)
            && c.Attempt.Source.ClientCaseId == caseId && c.Attempt.Source.IsActive
            && c.Attempt.Source.AcceptedScopeVersion == c.Attempt.Source.Scope.Version
            && c.Attempt.Version == c.Attempt.Source.CurrentAttemptVersion)
            .Select(c => new
            {
                c.AttemptId,
                c.SizeBytes,
                c.Sha256,
                c.Attempt.ActorId,
                c.Attempt.SourceId,
                AttemptVersion = c.Attempt.Version,
                c.Attempt.RequestSha256,
                c.Attempt.FrozenSnapshotSha256,
                c.Attempt.Source.DocumentKey,
                c.Attempt.Source.Scope.BindingUrl
            })
            .ToDictionaryAsync(c => c.AttemptId, ct);
        var retired = (await db.CaseDocumentUploadRetirements.AsNoTracking()
            .Where(r => r.CaseId == caseId && attemptIds.Contains(r.UploadKey)).Select(r => r.UploadKey).ToArrayAsync(ct)).ToHashSet();
        var authority = new Dictionary<int, bool>();
        var eligible = new List<Guid>();
        foreach (var candidate in candidates)
        {
            var receipt = candidate.Receipt;
            var snapshot = receipt.Snapshot;
            if (retired.Contains(snapshot.AttemptId) || !metadata.TryGetValue(snapshot.AttemptId, out var content)
                || content.SizeBytes != snapshot.ContentSizeBytes || content.Sha256 != snapshot.ContentSha256
                || content.ActorId != snapshot.ActorId || content.SourceId != snapshot.SourceId
                || content.AttemptVersion != snapshot.AttemptVersion || content.DocumentKey != snapshot.DocumentKey
                || content.BindingUrl != snapshot.BindingUrl || content.RequestSha256 != receipt.Generation.RequestSha256
                || content.FrozenSnapshotSha256 != Hash(receipt.Generation.FrozenDomainJson)) continue;
            if (!authority.TryGetValue(snapshot.ActorId, out var allowed))
                authority[snapshot.ActorId] = allowed = await CaseDocumentUploadAuthorization.CanPublishAsync(db, snapshot.ActorId, caseId, ct);
            if (allowed) eligible.Add(candidate.Operation.Id);
        }
        if (eligible.Count == 0) return [];
        var now = DateTime.UtcNow;
        // Dead-letter rows have no owner; a concurrent retry/claim must win this conditional fence.
        await db.StorageOperations.Where(o => eligible.Contains(o.Id)
                && o.Status == StorageOperationStatuses.DeadLetter && o.LastError == "Case publication: forbidden."
                && o.CompletedAt != null && o.LeaseOwner == null && o.LeaseExpiresAt == null
                && !db.StorageTombstones.Any(t => t.StorageObjectId == o.StorageObjectId)
                && db.StorageObjects.Any(obj => obj.Id == o.StorageObjectId && !obj.IsDeleted
                    && db.StorageObjectGenerations.Any(g => g.Id == o.StorageObjectGenerationId
                        && g.StorageObjectId == obj.Id && g.Generation == obj.CurrentGeneration)))
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, StorageOperationStatuses.Pending)
                .SetProperty(o => o.NextAttemptAt, now).SetProperty(o => o.CompletedAt, (DateTime?)null)
                .SetProperty(o => o.LastError, (string?)null).SetProperty(o => o.UpdatedAt, now), ct);
        return authority.Where(p => p.Value).Select(p => p.Key).ToArray();
    }

    private static bool ValidInput(CourtDocumentSourceInput input)
        => input is { RenderedHtml.Length: > 0 and <= ManagedCaseDocumentUploadService.MaxSize }
            && ValidMetadata(input.DocumentKey, input.FileName, input.Title, input.SourceUrl)
            && (input.LegacyDocumentKey is null || input.LegacyDocumentKey is { Length: 34 }
                && input.LegacyDocumentKey.StartsWith("sudrf-act-", StringComparison.Ordinal)
                && input.LegacyDocumentKey.Skip(10).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            && Encoding.UTF8.GetByteCount(input.RenderedHtml) <= ManagedCaseDocumentUploadService.MaxSize;

    private static bool ValidMetadata(string? key, string? fileName, string? title, string? sourceUrl)
        => key is { Length: 42 } && fileName is { Length: > 0 and <= 260 } && title is { Length: > 0 and <= 4000 }
            && key.StartsWith("sudrf-act-", StringComparison.Ordinal)
            && key.Skip(10).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && !fileName.Any(c => c < 32 || "/\\:*?\"<>|".Contains(c)) && ValidUrl(sourceUrl);

    private T TrackFresh<T>(T fresh) where T : class
    {
        var incoming = db.Entry(fresh);
        var key = incoming.Metadata.FindPrimaryKey()!.Properties;
        var tracked = db.ChangeTracker.Entries<T>().SingleOrDefault(e => key.All(p =>
            Equals(e.Property(p.Name).CurrentValue, incoming.Property(p.Name).CurrentValue)));
        if (tracked is null) { incoming.State = EntityState.Unchanged; return fresh; }
        tracked.CurrentValues.SetValues(fresh);
        tracked.OriginalValues.SetValues(fresh);
        return tracked.Entity;
    }

    private static bool ValidUrl(string? value) => value is { Length: > 0 and <= 1000 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;
    private static CourtDocumentSourceBatchResult Result(string status) => new(status, []);
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string RequestHash(GeneratedCourtDocumentSnapshot value)
        => Hash(FormattableString.Invariant($"court-event-summary:1:{value.CaseId}:{value.SourceId:N}:{value.AttemptId:N}:{value.AttemptVersion}"));
    private static string InputHash(IEnumerable<CourtDocumentSourceInput> inputs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var input in inputs)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                input.DocumentKey,
                input.FileName,
                input.Title,
                input.SourceUrl,
                ContentSizeBytes = Encoding.UTF8.GetByteCount(input.RenderedHtml),
                ContentSha256 = Hash(input.RenderedHtml)
            }, JsonOptions));
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length);
            hash.AppendData(bytes);
            if (input.LegacyDocumentKey is { } legacyKey)
            {
                var legacyBytes = Encoding.UTF8.GetBytes(legacyKey);
                BinaryPrimitives.WriteInt32BigEndian(length, legacyBytes.Length);
                hash.AppendData(length);
                hash.AppendData(legacyBytes);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
