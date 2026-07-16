using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PaymentsListSummaryDto
{
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal Balance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int Total { get; init; }
}

public sealed class PaymentListReferenceDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
}

public sealed class PaymentListLegalEntityDto
{
    public int Id { get; init; }
    public string ShortName { get; init; } = string.Empty;
    public string? FullName { get; init; }
    public string? Inn { get; init; }
}

public sealed class PaymentListCaseDto
{
    public int Id { get; init; }
    public string Title { get; init; } = string.Empty;
}

public sealed class PaymentListItemDto
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public DateTime PlannedDate { get; init; }
    public decimal Amount { get; init; }
    public PaymentType Type { get; init; }
    public PaymentStatus Status { get; init; }
    public string Description { get; init; } = string.Empty;
    public bool IsPaid { get; init; }
    public DateTime? PaidDate { get; init; }
    public DateTime? LastPaymentDate { get; init; }
    public string Notes { get; init; } = string.Empty;
    public string SystemNotes { get; init; } = string.Empty;
    public int RescheduleCount { get; init; }
    public decimal PaidAmount { get; init; }
    public PaymentAdvanceReviewStatus AdvanceReviewStatus { get; init; }
    public string? AdvanceReviewReason { get; init; }
    public DateTime? AdvanceReviewedAt { get; init; }
    public int? AdvanceReviewedByUserId { get; init; }
    public string? AdvanceReviewedByUserName { get; init; }
    public decimal OutstandingAmount => Amount > PaidAmount ? Math.Round(Amount - PaidAmount, 2) : 0m;
    public bool HasPartialPayment => PaidAmount > 0m && PaidAmount < Amount;
    public int? DelayDays => LastPaymentDate.HasValue
        ? (int)Math.Round((LastPaymentDate.Value.Date - PlannedDate.Date).TotalDays)
        : null;
    public DateTime CreatedAt { get; init; }
    public int? ClientId { get; init; }
    public int? LegalEntityId { get; init; }
    public string? LegalEntityName { get; init; }
    public string? LegalEntityInn { get; init; }
    public int? ExpenseClientId { get; init; }
    public int? ExpenseLegalEntityId { get; init; }
    public string? ExpenseLegalEntityName { get; init; }
    public string? ExpenseLegalEntityInn { get; init; }
    public int? ContractId { get; init; }
    public DocumentKind DocumentKind { get; init; }
    public string? PaymentOrderNumber { get; init; }
    public DateTime? PaymentDocumentDate { get; init; }
    public int? ClientCaseId { get; init; }
    public string? ClientCaseTitle { get; init; }
    public string? ClientCaseReference { get; init; }
    public bool IsNotForAccounting { get; init; }
    public string? CounterpartyName { get; set; }
    public int? ContractorUserId { get; set; }
    public string? ContractorUserName { get; set; }
    public int? DealTypeId { get; init; }
    public int? IncomeTypeId { get; init; }
    public int? ServiceId { get; init; }
    public int? PaymentSourceId { get; init; }
    public int? PaymentStatusId { get; init; }
    public int? InvoiceId { get; init; }
    public string? Account { get; init; }
    public DateTime? AccountDate { get; init; }
    public PaymentListReferenceDto? Client { get; init; }
    public PaymentListReferenceDto? ExpenseClient { get; init; }
    public PaymentListCaseDto? ClientCase { get; init; }
    public PaymentListReferenceDto? DealType { get; init; }
    public PaymentListReferenceDto? IncomeType { get; init; }
    public PaymentListReferenceDto? Service { get; init; }
    public PaymentListReferenceDto? PaymentSource { get; init; }
    public PaymentListReferenceDto? PaymentStatusEntity { get; init; }
    public PaymentListLegalEntityDto? LegalEntity { get; init; }
    public PaymentListLegalEntityDto? ExpenseLegalEntity { get; init; }
}

public sealed class PaymentsPageResponse : PagedResponse<PaymentListItemDto>
{
    public PaymentsListSummaryDto Summary { get; init; } = new();
}
