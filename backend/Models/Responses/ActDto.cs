using System;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class ActDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string CreationMode { get; set; } = ActCreationMode.Manual;
    public string? InvoiceNumber { get; set; }
    public int? InvoiceId { get; set; }
    public int? OriginalActId { get; set; }
    public string? OriginalActNumber { get; set; }
    public PaymentType? Type { get; set; }
    public int? ContractId { get; set; }
    public int? SellerCompanyId { get; set; }
    public int? SellerBranchId { get; set; }
    /// <summary>Реквизиты продавца уже зафиксированы в документе и больше не меняются.</summary>
    public bool SellerFrozen { get; set; }
    public string? SellerSnapshotPolicy { get; set; }
    public PayPlanner.Api.Services.Companies.SellerSnapshotMetadata? SellerSnapshotInfo { get; set; }
    public string? CounterpartyInn { get; set; }
    public ActStatus Status { get; set; }
    public string WorkflowStatus { get; set; } = ActWorkflowStatus.Draft;
    public DateTime? SentForSigningAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelledReason { get; set; }
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? LegalEntityId { get; set; }
    public string? LegalEntityName { get; set; }
    public string? LegalEntityInn { get; set; }
    public int? ResponsibleId { get; set; }
    public string? ResponsibleName { get; set; }
    public string? Comment { get; set; }
    public IReadOnlyCollection<ActLineItemDto> LineItems { get; set; } = Array.Empty<ActLineItemDto>();
    public IReadOnlyCollection<ActCorrectionLinkDto> CorrectionActs { get; set; } = Array.Empty<ActCorrectionLinkDto>();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? AuditCreatedByUserId { get; set; }
    public string? AuditCreatedByUserName { get; set; }
    public DateTime? AuditUpdatedAt { get; set; }
    public int? AuditUpdatedByUserId { get; set; }
    public string? AuditUpdatedByUserName { get; set; }
    public bool AuditIsDeleted { get; set; }
    public DateTime? AuditDeletedAt { get; set; }
    public int? AuditDeletedByUserId { get; set; }
    public string? AuditDeletedByUserName { get; set; }
}

public class ActCorrectionLinkDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string WorkflowStatus { get; set; } = ActWorkflowStatus.Draft;
}

public class ActLineItemDto
{
    public int? Id { get; set; }
    public int? InvoiceLineItemId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public int? SourceSortOrder { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "шт";
    public decimal Price { get; set; }
    public string? VatRate { get; set; }
}
