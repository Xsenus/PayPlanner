using System.Globalization;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Services.Storage;

// Called only by the managed document writer, inside its case publication
// transaction and after the required copies have been verified. No provider I/O.
public sealed class GeneratedFinancialDocumentPublication(PaymentContext db,
    CaseDocumentPublicationRecords records, GeneratedFinancialDocumentRegistry sources, IStoragePolicyService policies)
{
    internal static ManagedCaseDocumentGenerationSource? GenerationSource(ManagedCaseDocumentGeneration generation)
        => GeneratedFinancialDocumentRegistry.ReadProof(generation) is { } p
            ? new(p.CaseId, p.ActorId, p.UploadKey, GeneratedFinancialDocumentRegistry.DocumentKey(p),
                GeneratedFinancialDocumentRegistry.DocumentKey(p), GeneratedFinancialDocumentRegistry.FileName(p),
                "text/html; charset=utf-8", GeneratedFinancialDocumentRegistry.DocumentType(p.Kind)!, "Черновик", generation) : null;

    internal static bool SupportsIntent(ManagedCaseDocumentUploadService.UploadIntent? intent)
        => ManagedCaseDocumentUploadService.IsValidResumeIntent(intent) && intent!.Generation is { } generation
            && GenerationSource(generation) is { } source
            && source == ManagedCaseDocumentUploadService.GenerationSource(intent)
            && GeneratedFinancialDocumentRegistry.ReadProof(generation) is { } proof
            && intent.SizeBytes == proof.ContentSizeBytes && intent.Sha256 == proof.ContentSha256;

    internal static bool CanSchedule(string? json, string owner)
    {
        var intent = ManagedCaseDocumentUploadService.ParseIntent(json);
        return SupportsIntent(intent) && !intent!.DocumentId.HasValue
            && owner == $"generated:case:{intent.CaseId}:{intent.ActorId}:{intent.UploadKey:N}";
    }

    public async Task<bool> CanPublishAsync(ManagedCaseDocumentGenerationSource source, CancellationToken ct)
        => GenerationSource(source.Generation) == source
            && await CanPublishAsync(source.ActorId, source.CaseId, source.Generation, ct);

    public async Task<bool> CanPublishAsync(int actorId, int caseId, ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        var source = GenerationSource(generation);
        if (source is null || source.ActorId != actorId || source.CaseId != caseId
            || !await sources.CanPublishAsync(source, ct)) return false;
        var proof = GeneratedFinancialDocumentRegistry.ReadProof(generation)!;
        var publishedId = await db.GeneratedFinancialDocumentAttempts.AsNoTracking().Where(a => a.Id == proof.AttemptId)
            .Select(a => a.PublishedDocumentId).SingleAsync(ct);
        if (!publishedId.HasValue) return true;
        // A historical marker alone is not a winning financial receipt. Never
        // silently regenerate a deleted document or financial record on retry.
        if (!await db.ClientCaseDocuments.AsNoTracking().AnyAsync(d => d.Id == publishedId
                && d.ClientCaseId == caseId && d.UploadedByUserId == actorId && d.DocumentKey == source.DocumentKey
                && d.OriginalFileName == source.FileName && d.ContentType == source.ContentType
                && d.SizeBytes == proof.ContentSizeBytes, ct)) return false;
        return proof.Kind switch
        {
            "invoice" => await db.Payments.AsNoTracking().AnyAsync(p => p.Id == proof.EntityId
                && p.InvoiceId == proof.InvoiceEntityId && p.ClientCaseId == caseId
                && p.InvoiceClientCases.Any(c => c.ClientCaseId == caseId), ct),
            "act" => await db.Acts.AsNoTracking().AnyAsync(a => a.Id == proof.EntityId, ct),
            "contract" => await db.Contracts.AsNoTracking().AnyAsync(c => c.Id == proof.EntityId
                && c.ClientCaseContracts.Any(link => link.ClientCaseId == caseId), ct),
            _ => false
        };
    }

    public async Task PersistAsync(ClientCaseDocument document, ManagedCaseDocumentGeneration generation, CancellationToken ct)
    {
        var source = GenerationSource(generation);
        var proof = GeneratedFinancialDocumentRegistry.ReadProof(generation);
        if (db.Database.CurrentTransaction is null || source is null || proof is null
            || document.ClientCaseId != source.CaseId || document.UploadedByUserId != source.ActorId
            || document.DocumentKey != source.DocumentKey || document.OriginalFileName != source.FileName
            || document.DocumentType != source.DocumentType || document.Status != source.Status
            || document.ContentType != source.ContentType || document.SizeBytes != proof.ContentSizeBytes)
            throw new InvalidOperationException("Financial publication requires its exact source and transaction.");
        // The managed writer already holds the case lock. Follow the capture
        // lock order, and recheck claims before inserting any financial graph.
        if (await db.ClientCaseWorkOrders.Where(o => o.Id == proof.WorkOrderId && o.ClientCaseId == source.CaseId)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Title, o => o.Title), ct) != 1)
            throw new InvalidOperationException("Financial publication lost its work order.");
        var kind = proof.Kind switch
        {
            "invoice" => GeneratedFinancialDocumentKind.Invoice,
            "act" => GeneratedFinancialDocumentKind.Act,
            "contract" => GeneratedFinancialDocumentKind.Contract,
            _ => throw new InvalidOperationException("Unsupported financial proof kind.")
        };
        await new GeneratedFinancialClaims(db).LockNumberScopeAsync(kind, ct);
        var domain = await sources.ReadDomainAsync(generation, ct)
            ?? throw new InvalidOperationException("Financial publication lost its number or services.");
        var attempt = await db.GeneratedFinancialDocumentAttempts.SingleAsync(a => a.Id == proof.AttemptId, ct);
        if (attempt.PublishedDocumentId.HasValue || document.UploadedByName != domain.ActorName)
            throw new InvalidOperationException("Financial publication cannot replace an accepted winner or author.");
        var operationKey = StorageObjectService.GetCommitOperationKey($"case-document:{source.CaseId}:{source.ActorId}:{source.UploadKey:N}");
        var row = await db.StorageOperations.AsNoTracking().Where(o => o.IdempotencyKey == operationKey
                && o.OperationType == "commit" && o.Status == StorageOperationStatuses.Succeeded
                && !o.StorageObject.IsDeleted && !db.StorageTombstones.Any(t => t.StorageObjectId == o.StorageObjectId)
                && o.StorageObject.DataClass == "case-document" && o.StorageObject.OwnerType == "case-document"
                && o.StorageObject.OwnerId == $"generated:case:{source.CaseId}:{source.ActorId}:{source.UploadKey:N}"
                && o.StorageObjectGenerationId != null && o.StorageObjectGeneration!.StorageObjectId == o.StorageObjectId
                && o.StorageObjectGeneration.Generation == o.StorageObject.CurrentGeneration
                && o.StorageObjectGeneration.SizeBytes == proof.ContentSizeBytes && o.StorageObjectGeneration.Sha256 == proof.ContentSha256)
            .Select(o => new { o.PayloadJson, o.StorageObjectGenerationId }).SingleOrDefaultAsync(ct);
        var intent = ManagedCaseDocumentUploadService.ParseIntent(row?.PayloadJson);
        if (!SupportsIntent(intent) || intent!.Generation != generation || intent.DocumentId.HasValue
            || intent.ActorName != domain.ActorName)
            throw new InvalidOperationException("Financial publication requires its immutable content receipt.");
        var policy = policies.Evaluate("case-document");
        var writers = policy.WriteProviders.Select(p => p.ToLowerInvariant()).Distinct().ToArray();
        if (!policy.RoutingEnabled || !policy.IsValid || row is null
            || await db.StorageReplicas.AsNoTracking().Where(r => r.StorageObjectGenerationId == row.StorageObjectGenerationId
                && r.Status == StorageReplicaStatuses.Available && r.LastVerifiedAt != null
                && r.SizeBytes == proof.ContentSizeBytes && r.Sha256 == proof.ContentSha256 && writers.Contains(r.ProviderKey.ToLower()))
                .Select(r => r.ProviderKey.ToLower()).Distinct().CountAsync(ct) < policy.RequiredCopies)
            throw new InvalidOperationException("Financial publication requires its verified mandatory copies.");
        using var actorScope = db.UseAcceptedAuditActor(domain.ActorId, domain.ActorName);
        var clientCase = await db.ClientCases.SingleAsync(c => c.Id == source.CaseId, ct);
        await db.Entry(clientCase).ReloadAsync(ct);
        var label = domain.Kind switch { GeneratedFinancialDocumentKind.Invoice => "счет", GeneratedFinancialDocumentKind.Act => "акт", _ => "договор" };
        db.SetAuditReason($"По предварительному расчету дела #{domain.CaseId} создан {label} №{domain.Number}.");
        switch (domain.Kind)
        {
            case GeneratedFinancialDocumentKind.Invoice:
                var payment = BuildInvoice(domain);
                db.Payments.Add(payment);
                var order = await db.ClientCaseWorkOrders.SingleAsync(o => o.Id == domain.WorkOrderId, ct);
                await db.Entry(order).ReloadAsync(ct);
                order.Status = "invoice_created";
                order.UpdatedAt = DateTime.UtcNow;
                order.UpdatedByUserId = domain.ActorId;
                order.UpdatedByUserName = domain.ActorName;
                break;
            case GeneratedFinancialDocumentKind.Act:
                db.Acts.Add(BuildAct(domain));
                break;
            case GeneratedFinancialDocumentKind.Contract:
                db.Contracts.Add(BuildContract(domain));
                break;
            default: throw new InvalidOperationException("Unsupported financial kind.");
        }
        var amount = domain.Amount.ToString("0.##", CultureInfo.CurrentCulture);
        CaseDocumentPublicationRecords.AppendEvent(clientCase,
            $"По предварительному расчету «{domain.WorkOrderTitle}» создан {label} №{domain.Number} на сумму {amount} {domain.Currency}. Создал: {domain.ActorName}.",
            DateTime.SpecifyKind(document.UploadedAt, DateTimeKind.Utc).ToLocalTime());
        await db.SaveChangesAsync(ct);
        // Written in the same transaction as finance/document/binding/outbox.
        // Rollback retains the original pending claims and frozen evidence.
        attempt.PublishedDocumentId = document.Id;
        await records.PersistGeneratedFinancialAsync(clientCase, document, domain, ct);
    }

    private static Payment BuildInvoice(GeneratedFinancialDomainSnapshot d)
    {
        var payment = new Payment
        {
            Id = d.Identity.EntityId,
            Account = d.Number,
            AccountDate = d.Date,
            InvoiceId = d.Identity.InvoiceEntityId,
            Invoice = new Invoice
            {
                Id = d.Identity.InvoiceEntityId!.Value,
                Number = d.Number,
                Date = d.Date,
                CreatedAt = d.CreatedAt
            },
            Date = d.DueDate!.Value,
            PlannedDate = d.DueDate.Value,
            Amount = d.Amount,
            PaidAmount = 0m,
            Status = PaymentStatus.Pending,
            InvoiceWorkflowStatus = InvoiceWorkflowStatus.Draft,
            Type = PaymentType.Income,
            DocumentKind = DocumentKind.Invoice,
            SellerCompanyId = d.SellerCompanyId,
            SellerBranchId = d.SellerBranchId,
            SellerSnapshotJson = d.SellerSnapshotJson,
            SellerSnapshotPolicy = d.SellerSnapshotPolicy,
            Description = d.Description ?? throw new InvalidOperationException("Accepted invoice description is missing."),
            ClientId = d.ClientId,
            LegalEntityId = d.LegalEntityId,
            ClientCaseId = d.CaseId,
            ClientCaseReference = d.CaseReference,
            CreatedAt = d.CreatedAt
        };
        payment.InvoiceClientCases.Add(new() { ClientCaseId = d.CaseId });
        foreach (var executor in d.Lines.Where(l => l.ExecutorUserId.HasValue).Select(l => l.ExecutorUserId!.Value).Distinct())
            payment.Responsibles.Add(new() { UserId = executor });
        var sort = 1;
        foreach (var line in d.Lines)
            payment.InvoiceLineItems.Add(new()
            {
                Name = line.Name,
                Quantity = line.Quantity,
                Price = line.Price,
                Unit = "шт",
                SortOrder = sort++,
                CreatedAt = d.CreatedAt
            });
        PaymentDomainService.Normalize(payment);
        return payment;
    }

    private static Act BuildAct(GeneratedFinancialDomainSnapshot d)
    {
        var act = new Act
        {
            Id = d.Identity.EntityId,
            Number = d.Number,
            SellerCompanyId = d.SellerCompanyId,
            SellerBranchId = d.SellerBranchId,
            SellerSnapshotJson = d.SellerSnapshotJson,
            SellerSnapshotPolicy = d.SellerSnapshotPolicy,
            Title = d.Title,
            Date = d.Date,
            Amount = d.Amount,
            CreationMode = ActCreationMode.Manual,
            Status = ActStatus.Created,
            WorkflowStatus = ActWorkflowStatus.Draft,
            ClientId = d.ClientId,
            LegalEntityId = d.LegalEntityId,
            ResponsibleId = d.Lines.FirstOrDefault(l => l.ExecutorUserId.HasValue)?.ExecutorUserId,
            Comment = d.Description,
            CreatedAt = d.CreatedAt
        };
        var sort = 1;
        foreach (var line in d.Lines)
            act.LineItems.Add(new()
            {
                SourceKey = GeneratedFinancialClaims.SourceKey(d.WorkOrderId, line.WorkOrderItemId),
                SourceSortOrder = line.SourceSortOrder,
                Name = line.Name,
                Quantity = line.Quantity,
                Price = line.Price,
                Unit = "шт",
                SortOrder = sort++,
                CreatedAt = d.CreatedAt
            });
        return act;
    }

    private static Contract BuildContract(GeneratedFinancialDomainSnapshot d)
    {
        var contract = new Contract
        {
            Id = d.Identity.EntityId,
            Number = d.Number,
            SellerCompanyId = d.SellerCompanyId,
            SellerBranchId = d.SellerBranchId,
            SellerSnapshotJson = d.SellerSnapshotJson,
            SellerSnapshotPolicy = d.SellerSnapshotPolicy,
            Title = d.Title,
            Date = d.Date,
            StartDate = d.StartDate!.Value,
            ValidUntil = d.ValidUntil,
            Description = d.Description,
            BillingMode = ContractBillingMode.Fixed,
            Status = ContractStatus.Draft,
            IsOpenEnded = d.IsOpenEnded,
            Amount = d.Amount,
            CreatedAt = d.CreatedAt
        };
        var comment = $"Создан из заказ-наряда «{d.WorkOrderTitle}» по делу {d.CaseTitle}.";
        if (d.ClientId.HasValue) contract.ClientContracts.Add(new() { ClientId = d.ClientId.Value, Comment = comment });
        if (d.LegalEntityId.HasValue) contract.LegalEntityContracts.Add(new() { LegalEntityId = d.LegalEntityId.Value, Comment = comment });
        contract.ClientCaseContracts.Add(new() { ClientCaseId = d.CaseId });
        return contract;
    }
}
