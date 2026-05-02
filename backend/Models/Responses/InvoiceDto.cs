using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class InvoiceDto
{
    public int Id { get; set; }
    public int? InvoiceEntityId { get; set; }
    public string Number { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public DateTime? DueDate { get; set; }
    public decimal Amount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public PaymentType Type { get; set; }
    public PaymentStatus Status { get; set; }
    public string WorkflowStatus { get; set; } = InvoiceWorkflowStatus.Draft;
    public DateTime? SentForSigningAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public int? CancelledByUserId { get; set; }
    public string? CancelledReason { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidDate { get; set; }
    public int? ClientId { get; set; }
    public int? ExpenseClientId { get; set; }
    public int? ContractId { get; set; }
    public string? ClientName { get; set; }
    public string? ClientCompany { get; set; }
    public int? ClientStatusId { get; set; }
    public ClientStatusDto? ClientStatus { get; set; }
    public string? ExpenseClientName { get; set; }
    public string? ExpenseClientCompany { get; set; }
    public int? ExpenseClientStatusId { get; set; }
    public ClientStatusDto? ExpenseClientStatus { get; set; }
    public int? ClientCaseId { get; set; }
    public string? ClientCaseTitle { get; set; }
    public List<int> ClientCaseIds { get; set; } = new();
    public List<InvoiceClientCaseDto> ClientCases { get; set; } = new();
    public string? ClientCaseReference { get; set; }
    public string? Description { get; set; }
    public bool IsNotForAccounting { get; set; }
    public string? ActReference { get; set; }
    public int? ActId { get; set; }
    public string? ActNumber { get; set; }
    public string? ActTitle { get; set; }
    public ActStatus? ActStatus { get; set; }
    public string? ActWorkflowStatus { get; set; }
    public List<int> ActIds { get; set; } = new();
    public List<InvoiceActDto> Acts { get; set; } = new();
    public string? ResponsibleName { get; set; }
    public List<InvoiceResponsibleDto> Responsibles { get; set; } = new();
    public int? ResponsibleId { get; set; }
    public string? CounterpartyInn { get; set; }
    public string? CounterpartyName { get; set; }
    public int? ServiceId { get; set; }
    public string? ServiceName { get; set; }
    public string? PaymentStatusName { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? AuditCreatedByUserId { get; set; }
    public string? AuditCreatedByUserName { get; set; }
    public DateTime? AuditUpdatedAt { get; set; }
    public int? AuditUpdatedByUserId { get; set; }
    public string? AuditUpdatedByUserName { get; set; }
    public bool AuditIsDeleted { get; set; }
    public DateTime? AuditDeletedAt { get; set; }
    public int? AuditDeletedByUserId { get; set; }
    public string? AuditDeletedByUserName { get; set; }
    public DocumentKind DocumentKind { get; set; }
    public List<InvoiceLineItemDto> LineItems { get; set; } = new();
}

public class InvoiceClientCaseDto
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Title { get; set; } = string.Empty;
    public ClientCaseStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class InvoiceActDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string CreationMode { get; set; } = ActCreationMode.Manual;
    public ActStatus Status { get; set; }
    public string WorkflowStatus { get; set; } = ActWorkflowStatus.Draft;
    public int? ClientId { get; set; }
    public string? ClientName { get; set; }
    public int? ResponsibleId { get; set; }
    public string? ResponsibleName { get; set; }
    public string? Comment { get; set; }
    public IReadOnlyCollection<ActLineItemDto> LineItems { get; set; } = Array.Empty<ActLineItemDto>();
}

public class InvoiceLineItemDto
{
    public int? Id { get; set; }
    public int? SortOrder { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public int? ServiceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "шт";
    public decimal Price { get; set; }
}

public class InvoiceResponsibleDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
}
