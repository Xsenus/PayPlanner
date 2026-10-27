using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Extensions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Companies;

namespace PayPlanner.Api.Services.Storage;

public sealed record GeneratedFinancialLine(int WorkOrderItemId, string Name, decimal Quantity,
    decimal Price, int SourceSortOrder, int? ExecutorUserId);
public sealed record GeneratedFinancialDomainSnapshot(int Version, int CaseId, int WorkOrderId, int ActorId,
    string ActorName, GeneratedFinancialDocumentKind Kind, GeneratedFinancialIdentity Identity,
    string Number, DateTime Date, DateTime? DueDate, DateTime? StartDate, DateTime? ValidUntil, bool IsOpenEnded,
    string? Title, string? Description, decimal Amount, int? ClientId, int? LegalEntityId,
    string CaseReference, string CaseTitle, string WorkOrderTitle, string Currency, DateTime CreatedAt,
    GeneratedFinancialLine[] Lines, int? SellerCompanyId = null, int? SellerBranchId = null,
    string? SellerSnapshotJson = null, string? SellerSnapshotPolicy = null);
public sealed record GeneratedFinancialDocumentCapture(GeneratedFinancialDomainSnapshot Domain, string Html);
public sealed record GeneratedFinancialDocumentProof(int Version, Guid AttemptId, Guid UploadKey,
    int CaseId, int WorkOrderId, int ActorId, string Kind, int EntityId, int? InvoiceEntityId,
    string DomainSha256, long ContentSizeBytes, string ContentSha256);
public sealed record GeneratedFinancialDocumentReceipt(GeneratedFinancialDocumentProof Proof, ManagedCaseDocumentGeneration Generation);
public sealed record GeneratedFinancialCaptureResult(string Status, GeneratedFinancialDocumentReceipt? Receipt = null, bool IsReplay = false);
public sealed record GeneratedFinancialFrozenContent(GeneratedFinancialDomainSnapshot Domain, byte[] Bytes);

// A server-only capture callback runs under the short case lock. It may perform
// bounded domain queries and render HTML, but must never call a storage provider.
public sealed class GeneratedFinancialDocumentRegistry(PaymentContext db)
{
    internal const string GenerationKind = "financial";
    internal const int MaximumDomainCharacters = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GeneratedFinancialCaptureResult> CaptureAsync(int caseId, int workOrderId, int actorId,
        string actorName, Guid uploadKey, GeneratedFinancialDocumentKind kind, string requestSha256,
        Func<ClientCase, ClientCaseWorkOrder, GeneratedFinancialIdentity, CancellationToken, Task<GeneratedFinancialDocumentCapture>> capture,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (db.Database.CurrentTransaction is not null || db.ChangeTracker.HasChanges())
            throw new InvalidOperationException("Financial capture requires a clean, independent unit of work.");
        var kindName = KindName(kind);
        if (caseId <= 0 || workOrderId <= 0 || actorId <= 0 || uploadKey == Guid.Empty || kindName is null
            || string.IsNullOrWhiteSpace(actorName) || actorName.Length > 256 || !IsSha(requestSha256)) return new("invalid");
        if (!await CanCaptureAsync(actorId, caseId, workOrderId, kind, ct)) return new("forbidden");
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, caseId, actorId, uploadKey, ct)) return new("retired");
        var existing = await FindAttemptAsync(caseId, actorId, uploadKey, ct);
        if (existing is not null) return await ReplayAsync(existing);
        var trackedBefore = db.ChangeTracker.Entries().Select(e => (e.Entity, Current: e.CurrentValues.Clone(), Original: e.OriginalValues.Clone())).ToArray();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var commitStarted = false;
        try
        {
            if (!await LockSourceAsync(db, caseId, workOrderId, ct)) return new("notFound");
            if (!await CanCaptureAsync(actorId, caseId, workOrderId, kind, ct)) return new("forbidden");
            if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, caseId, actorId, uploadKey, ct)) return new("retired");
            existing = await FindAttemptAsync(caseId, actorId, uploadKey, ct);
            if (existing is not null) return await ReplayAsync(existing);
            var claims = new GeneratedFinancialClaims(db);
            await claims.LockNumberScopeAsync(kind, ct);
            var clientCase = await db.ClientCases.AsNoTracking().Include(c => c.Client).Include(c => c.LegalEntity)
                .Include(c => c.ResponsibleUser).SingleAsync(c => c.Id == caseId, ct);
            var order = await db.ClientCaseWorkOrders.AsNoTracking().Include(o => o.Items)
                .SingleAsync(o => o.Id == workOrderId && o.ClientCaseId == caseId, ct);
            var status = WorkOrderStatusRules.Normalize(order.Status);
            if (kind == GeneratedFinancialDocumentKind.Invoice ? status != "approved" : status is not ("approved" or "invoice_created"))
                return new("conflict");
            // Exact replay was resolved above. The case/order locks serialize new
            // invoice attempts across authors while their required copies are pending.
            if (kind == GeneratedFinancialDocumentKind.Invoice
                && await HasActiveInvoiceAsync(db, caseId, workOrderId, ct)) return new("conflict");
            var identity = await new GeneratedFinancialIdentityAllocator(db).AllocateAsync(kind, ct);
            var captured = await capture(clientCase, order, identity, ct);
            if (db.ChangeTracker.HasChanges())
                throw new InvalidOperationException("Financial capture must not mutate a tracked domain graph.");
            if (!ValidDomain(captured.Domain, clientCase, order, actorId, actorName, kind, identity)
                || string.IsNullOrEmpty(captured.Html) || Encoding.UTF8.GetByteCount(captured.Html) > 26214400) return new("invalid");
            var json = JsonSerializer.Serialize(captured.Domain, JsonOptions);
            if (json.Length > MaximumDomainCharacters) return new("invalid");
            var bytes = Encoding.UTF8.GetBytes(captured.Html);
            var attempt = new GeneratedFinancialDocumentAttempt
            {
                UploadKey = uploadKey,
                ClientCaseId = caseId,
                WorkOrderId = workOrderId,
                ActorId = actorId,
                ActorName = actorName,
                Kind = kindName,
                EntityId = identity.EntityId,
                InvoiceEntityId = identity.InvoiceEntityId,
                RequestSha256 = requestSha256,
                FrozenDomainJson = json,
                FrozenDomainSha256 = Hash(json),
                ContentSizeBytes = bytes.LongLength,
                ContentSha256 = Hash(bytes)
            };
            if (!await claims.ClaimAsync(attempt, captured.Domain, ct)) return new("conflict");
            db.GeneratedFinancialDocumentAttempts.Add(attempt);
            var content = new GeneratedFinancialDocumentContent { Attempt = attempt, AttemptId = attempt.Id, Bytes = bytes };
            db.GeneratedFinancialDocumentContents.Add(content);
            await db.SaveChangesAsync(ct);
            if (!await CanCaptureAsync(actorId, caseId, workOrderId, kind, ct))
                throw new InvalidOperationException("Financial capture lost its original author's authority.");
            var receipt = TryReceipt(attempt) ?? throw new InvalidOperationException("Invalid server financial capture.");
            commitStarted = true;
            await transaction.CommitAsync(ct);
            db.Entry(content).State = EntityState.Detached;
            return new("recorded", receipt);
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
            catch (Exception rollback) { original.Data["FinancialCaptureRollbackFailure"] = rollback; }
            if (rolledBack || !commitStarted)
            {
                var originals = trackedBefore.Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
                foreach (var entry in db.ChangeTracker.Entries().ToArray())
                    if (!originals.Contains(entry.Entity)) entry.State = EntityState.Detached;
                foreach (var saved in trackedBefore)
                {
                    var entry = db.Entry(saved.Entity);
                    entry.CurrentValues.SetValues(saved.Current);
                    entry.OriginalValues.SetValues(saved.Original);
                    entry.State = EntityState.Unchanged;
                }
            }
            throw;
        }

        async Task<GeneratedFinancialCaptureResult> ReplayAsync(GeneratedFinancialDocumentAttempt attempt)
        {
            if (attempt.WorkOrderId != workOrderId || attempt.Kind != kindName || attempt.RequestSha256 != requestSha256)
                return new("conflict");
            var receipt = TryReceipt(attempt);
            var domain = ReadDomain(attempt);
            return receipt is null || domain is null || !await new GeneratedFinancialClaims(db).OwnsAsync(attempt, domain, ct)
                ? new("conflict") : new("recorded", receipt, true);
        }
    }

    internal static Task<bool> HasActiveInvoiceAsync(PaymentContext db, int caseId, int workOrderId, CancellationToken ct)
        => HasActiveAttemptAsync(db, caseId, workOrderId, GeneratedFinancialDocumentKind.Invoice, ct);

    internal static Task<bool> HasActiveAttemptAsync(PaymentContext db, int caseId, int workOrderId,
        GeneratedFinancialDocumentKind kind, CancellationToken ct)
        => db.GeneratedFinancialDocumentAttempts.AsNoTracking().AnyAsync(a => a.ClientCaseId == caseId
            && a.WorkOrderId == workOrderId && a.Kind == KindName(kind) && a.PublishedDocumentId == null
            && !db.CaseDocumentUploadRetirements.Any(r => r.CaseId == a.ClientCaseId
                && r.ActorId == a.ActorId && r.UploadKey == a.UploadKey), ct);

    internal static async Task<bool> LockSourceAsync(PaymentContext db, int caseId, int workOrderId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Financial source locks require a transaction.");
        if (await db.ClientCases.Where(c => c.Id == caseId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Title, c => c.Title), ct) != 1) return false;
        return await db.ClientCaseWorkOrders.Where(o => o.Id == workOrderId && o.ClientCaseId == caseId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Title, o => o.Title), ct) == 1;
    }

    public async Task<bool> CanPublishAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
    {
        var proof = ReadProof(source.Generation);
        if (proof is null || source.ActorId != proof.ActorId || source.CaseId != proof.CaseId || source.UploadKey != proof.UploadKey
            || source.DocumentKey != DocumentKey(proof) || source.RequestedDocumentKey != source.DocumentKey
            || source.FileName != FileName(proof) || source.DocumentType != DocumentType(proof.Kind)
            || source.Status != "Черновик" || source.ContentType != "text/html; charset=utf-8") return false;
        return await ValidateProofAsync(proof, source.Generation.RequestSha256, ct) is not null;
    }

    public async Task<GeneratedFinancialFrozenContent?> ReadFrozenAsync(GeneratedFinancialDocumentReceipt receipt, CancellationToken ct)
    {
        if (ReadProof(receipt.Generation) is not { } proof || proof != receipt.Proof) return null;
        var attempt = await ValidateProofAsync(proof, receipt.Generation.RequestSha256, ct);
        if (attempt is null) return null;
        var content = await db.GeneratedFinancialDocumentContents.AsNoTracking().SingleOrDefaultAsync(c => c.AttemptId == proof.AttemptId, ct);
        if (content is null || content.Bytes.LongLength != proof.ContentSizeBytes || Hash(content.Bytes) != proof.ContentSha256) return null;
        var domain = ReadDomain(attempt);
        return domain is not null && await CanCaptureAsync(proof.ActorId, proof.CaseId, proof.WorkOrderId, ParseKind(proof.Kind)!.Value, ct)
            ? new(domain, content.Bytes) : null;
    }

    // Metadata only: the publisher checks the immutable storage generation
    // receipt separately and must not load a potentially large HTML BLOB under locks.
    public async Task<GeneratedFinancialDomainSnapshot?> ReadDomainAsync(ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        if (ReadProof(generation) is not { } proof) return null;
        var attempt = await ValidateProofAsync(proof, generation.RequestSha256, ct);
        return attempt is null ? null : ReadDomain(attempt);
    }

    // Any occupant of the exact commit key prevents restoring file bytes through a new write.
    public Task<bool> HasFileCommitAsync(GeneratedFinancialDocumentReceipt receipt, CancellationToken ct)
    {
        var proof = receipt.Proof;
        var key = StorageObjectService.GetCommitOperationKey(
            $"case-document:{proof.CaseId}:{proof.ActorId}:{proof.UploadKey:N}");
        return db.StorageOperations.AsNoTracking().AnyAsync(o => o.IdempotencyKey == key, ct);
    }

    private async Task<GeneratedFinancialDocumentAttempt?> ValidateProofAsync(GeneratedFinancialDocumentProof proof, string requestSha, CancellationToken ct)
    {
        var kind = ParseKind(proof.Kind);
        if (!kind.HasValue || !IsSha(requestSha) || !await CanCaptureAsync(proof.ActorId, proof.CaseId, proof.WorkOrderId, kind.Value, ct)) return null;
        if (await CaseDocumentUploadRetirementService.IsRetiredAsync(db, proof.CaseId, proof.ActorId, proof.UploadKey, ct)) return null;
        var attempt = await db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == proof.AttemptId, ct);
        var receipt = attempt is null ? null : TryReceipt(attempt);
        var domain = attempt is null ? null : ReadDomain(attempt);
        return receipt?.Proof == proof && attempt!.RequestSha256 == requestSha && domain is not null
            && await new GeneratedFinancialClaims(db).CanPublishAsync(attempt, domain, ct) ? attempt : null;
    }

    private async Task<bool> CanCaptureAsync(int actorId, int caseId, int workOrderId, GeneratedFinancialDocumentKind kind, CancellationToken ct)
    {
        var section = kind switch
        {
            GeneratedFinancialDocumentKind.Invoice => RolePermissionCatalog.Accounts,
            GeneratedFinancialDocumentKind.Act => RolePermissionCatalog.Acts,
            GeneratedFinancialDocumentKind.Contract => RolePermissionCatalog.Contracts,
            _ => null
        };
        if (section is null || !await CaseDocumentUploadAuthorization.CanPublishAsync(db, actorId, caseId, ct)) return false;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier,
            actorId.ToString(CultureInfo.InvariantCulture))], "financial-document-capture"));
        var access = await db.ResolveSectionAccessAsync(principal, section, ct);
        return access?.Permission?.CanCreate == true && await db.ClientCaseWorkOrders.AsNoTracking()
            .AnyAsync(o => o.Id == workOrderId && o.ClientCaseId == caseId, ct);
    }

    private Task<GeneratedFinancialDocumentAttempt?> FindAttemptAsync(int caseId, int actorId, Guid uploadKey, CancellationToken ct)
        => db.GeneratedFinancialDocumentAttempts.AsNoTracking().SingleOrDefaultAsync(a => a.ClientCaseId == caseId
            && a.ActorId == actorId && a.UploadKey == uploadKey, ct);

    internal static GeneratedFinancialDocumentProof? ReadProof(ManagedCaseDocumentGeneration generation)
    {
        if (generation.Kind != GenerationKind || !IsSha(generation.RequestSha256) || generation.FrozenDomainJson is not { Length: > 0 and <= 48000 }) return null;
        try
        {
            var proof = JsonSerializer.Deserialize<GeneratedFinancialDocumentProof>(generation.FrozenDomainJson, JsonOptions);
            return proof is { Version: 1, CaseId: > 0, WorkOrderId: > 0, ActorId: > 0, EntityId: > 0, ContentSizeBytes: > 0 and <= 26214400 }
                && proof.AttemptId != Guid.Empty && proof.UploadKey != Guid.Empty && ParseKind(proof.Kind) is not null
                && (proof.Kind == "invoice" ? proof.InvoiceEntityId > 0 : proof.InvoiceEntityId is null)
                && IsSha(proof.DomainSha256) && IsSha(proof.ContentSha256)
                && JsonSerializer.Serialize(proof, JsonOptions) == generation.FrozenDomainJson ? proof : null;
        }
        catch (JsonException) { return null; }
    }

    private static GeneratedFinancialDocumentReceipt? TryReceipt(GeneratedFinancialDocumentAttempt attempt)
    {
        var domain = ReadDomain(attempt);
        if (domain is null || domain.CaseId != attempt.ClientCaseId || domain.WorkOrderId != attempt.WorkOrderId
            || domain.ActorId != attempt.ActorId || domain.ActorName != attempt.ActorName || KindName(domain.Kind) != attempt.Kind
            || domain.Identity != new GeneratedFinancialIdentity(attempt.EntityId, attempt.InvoiceEntityId)) return null;
        var proof = new GeneratedFinancialDocumentProof(1, attempt.Id, attempt.UploadKey, attempt.ClientCaseId,
            attempt.WorkOrderId, attempt.ActorId, attempt.Kind, attempt.EntityId, attempt.InvoiceEntityId,
            attempt.FrozenDomainSha256, attempt.ContentSizeBytes, attempt.ContentSha256);
        return MetadataReceipt(proof, attempt.RequestSha256);
    }

    // Acceptance metadata suffices to stop an owned attempt. Publication and
    // replay still validate the frozen domain, claims and content separately.
    internal static GeneratedFinancialDocumentReceipt? MetadataReceipt(GeneratedFinancialDocumentProof proof, string requestSha256)
    {
        var generation = new ManagedCaseDocumentGeneration(GenerationKind, requestSha256, JsonSerializer.Serialize(proof, JsonOptions));
        return ReadProof(generation) == proof ? new(proof, generation) : null;
    }

    private static GeneratedFinancialDomainSnapshot? ReadDomain(GeneratedFinancialDocumentAttempt attempt)
    {
        if (attempt.FrozenDomainJson is not { Length: > 0 and <= MaximumDomainCharacters }
            || !IsSha(attempt.FrozenDomainSha256) || Hash(attempt.FrozenDomainJson) != attempt.FrozenDomainSha256) return null;
        try { return JsonSerializer.Deserialize<GeneratedFinancialDomainSnapshot>(attempt.FrozenDomainJson, JsonOptions) is { } value && ValidShape(value) ? value : null; }
        catch (JsonException) { return null; }
    }

    private static bool ValidDomain(GeneratedFinancialDomainSnapshot value, ClientCase clientCase, ClientCaseWorkOrder order,
        int actorId, string actorName, GeneratedFinancialDocumentKind kind, GeneratedFinancialIdentity identity)
    {
        if (!ValidShape(value) || value.CaseId != clientCase.Id || value.WorkOrderId != order.Id || value.ActorId != actorId
            || value.ActorName != actorName || value.Kind != kind || value.Identity != identity || value.ClientId != clientCase.ClientId
            || value.LegalEntityId != clientCase.LegalEntityId || value.WorkOrderTitle != order.Title || value.Currency != order.Currency
            || value.CaseTitle != clientCase.Title || value.CaseReference != (string.IsNullOrWhiteSpace(clientCase.CaseNumber) ? clientCase.Title : clientCase.CaseNumber.Trim())) return false;
        var items = order.Items.ToDictionary(i => i.Id);
        if (value.Lines.Any(l => !items.TryGetValue(l.WorkOrderItemId, out var i) || l.Name != i.ServiceName || l.Quantity != i.Quantity
            || l.Price != i.UnitPrice || l.SourceSortOrder != i.SortOrder || l.ExecutorUserId != i.ExecutorUserId)
            || kind != GeneratedFinancialDocumentKind.Act && value.Lines.Length != items.Count) return false;
        var sortedIds = value.Lines.OrderBy(l => l.SourceSortOrder).ThenBy(l => l.WorkOrderItemId).Select(l => l.WorkOrderItemId);
        if (!sortedIds.SequenceEqual(value.Lines.Select(l => l.WorkOrderItemId))) return false;
        var amount = kind == GeneratedFinancialDocumentKind.Act
            ? decimal.Round(value.Lines.Sum(l => l.Quantity * l.Price), 2, MidpointRounding.AwayFromZero)
            : decimal.Round(value.Lines.Sum(l => decimal.Round(l.Quantity * l.Price, 2, MidpointRounding.AwayFromZero)), 2, MidpointRounding.AwayFromZero);
        return value.Amount == amount;
    }

    private static bool ValidShape(GeneratedFinancialDomainSnapshot value)
        => value is
        {
            Version: 1, CaseId: > 0, WorkOrderId: > 0, ActorId: > 0, Amount: > 0,
            Identity.EntityId: > 0, Lines.Length: > 0
        } && KindName(value.Kind) is not null
            && (value.Kind == GeneratedFinancialDocumentKind.Invoice ? value.Identity.InvoiceEntityId > 0 : value.Identity.InvoiceEntityId is null)
            && !string.IsNullOrWhiteSpace(value.Number) && value.ActorName is { Length: > 0 and <= 256 }
            && value.WorkOrderTitle is { Length: <= 200 } && value.Currency is { Length: 3 }
            && value.CaseReference is not null && value.CaseTitle is not null && value.ClientId is not <= 0 && value.LegalEntityId is not <= 0
            && value.CreatedAt.Kind == DateTimeKind.Utc && value.CreatedAt >= DateTime.UnixEpoch && value.Date.TimeOfDay == TimeSpan.Zero
            && (value.Kind != GeneratedFinancialDocumentKind.Invoice || value.DueDate.HasValue)
            && (value.Kind != GeneratedFinancialDocumentKind.Contract || value.StartDate.HasValue && (value.ClientId.HasValue || value.LegalEntityId.HasValue))
            && value.Lines.All(l => l is { WorkOrderItemId: > 0, Name.Length: <= 2000 } && l.ExecutorUserId is not <= 0)
            && value.Lines.Select(l => l.WorkOrderItemId).Distinct().Count() == value.Lines.Length
            && ValidSellerSnapshot(value);

    private static bool ValidSellerSnapshot(GeneratedFinancialDomainSnapshot value)
    {
        // Accepted historical captures remain replayable byte-for-byte. A new
        // profile/setting must never be used to backfill their missing fields.
        if (value.SellerSnapshotPolicy is null)
            return value.SellerCompanyId is null && value.SellerBranchId is null && value.SellerSnapshotJson is null;
        if (!DocumentSellerSnapshotPolicy.IsValid(value.SellerSnapshotPolicy)) return false;
        if (value.SellerSnapshotJson is null)
            return value.SellerSnapshotPolicy == DocumentSellerSnapshotPolicy.FirstFile
                && value.SellerCompanyId is null && value.SellerBranchId is null;
        if (value.SellerSnapshotJson.Length > DocumentSellerService.MaxSnapshotLength || value.SellerCompanyId is not > 0
            || value.SellerBranchId is <= 0) return false;
        var metadata = SellerLegalEntityComposer.ReadMetadata(value.SellerSnapshotJson);
        return metadata is { LegacyImages: false } && metadata.FrozenAtUtc is { Kind: DateTimeKind.Utc }
            && metadata.FreezeReason == value.SellerSnapshotPolicy
            && SellerLegalEntityComposer.FromSnapshot(value.SellerSnapshotJson, null)?.Id == value.SellerCompanyId;
    }

    internal static string DocumentKey(GeneratedFinancialDocumentProof proof) => $"{proof.Kind}-{proof.EntityId}";
    internal static string FileName(GeneratedFinancialDocumentProof proof) => $"case-{proof.CaseId}-{proof.Kind}-{proof.EntityId}.html";
    internal static string? DocumentType(string kind) => kind switch { "invoice" => "Счет", "act" => "Акт", "contract" => "Договор", _ => null };
    private static string? KindName(GeneratedFinancialDocumentKind kind) => kind switch
    { GeneratedFinancialDocumentKind.Invoice => "invoice", GeneratedFinancialDocumentKind.Act => "act", GeneratedFinancialDocumentKind.Contract => "contract", _ => null };
    private static GeneratedFinancialDocumentKind? ParseKind(string kind) => kind switch
    { "invoice" => GeneratedFinancialDocumentKind.Invoice, "act" => GeneratedFinancialDocumentKind.Act, "contract" => GeneratedFinancialDocumentKind.Contract, _ => null };
    private static bool IsSha(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
