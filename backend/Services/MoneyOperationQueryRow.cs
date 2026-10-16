namespace PayPlanner.Api.Services;

internal sealed class MoneyOperationQueryRow
{
    public int Id { get; init; }
    public string Source { get; init; } = string.Empty;
    public string Direction { get; init; } = string.Empty;
    public string AccountingType { get; init; } = string.Empty;
    public int? MoneyAccountId { get; init; }
    public int? CompanyId { get; init; }
    public string? MoneyAccountName { get; init; }
    public string? AccountName { get; init; }
    public string? CounterpartyName { get; init; }
    public int? VisibilityResponsibleUserId { get; init; }
    public int? ResponsibleUserId { get; init; }
    public string? ResponsibleName { get; init; }
    public int? IncomeTypeId { get; init; }
    public string? CategoryName { get; init; }
    public decimal Amount { get; init; }
    public decimal? VatRate { get; init; }
    public decimal? VatAmount { get; init; }
    public string? Currency { get; init; }
    public string? PaymentOrderNumber { get; init; }
    public DateTime Date { get; init; }
    public DateTime? RecognitionDate { get; init; }
    public bool IsAdvancePayment { get; init; }
    public string? Purpose { get; init; }
    public string? PaidInvoiceNumber { get; init; }
    public decimal? PaidAmount { get; init; }
    public string? ProjectName { get; init; }
    public string? DirectionName { get; init; }
    public string? SalesPointName { get; init; }
    public int? PaymentPriority { get; init; }
    public string? PaidAccountName { get; init; }
    public int? LinkedInvoiceId { get; init; }
    public bool IsInvoiceLinkProtected { get; init; }
    public string? LinkedInvoiceNumber { get; init; }
    public DateTime? LinkedInvoiceDate { get; init; }
    public int? LinkedContractId { get; init; }
    public string? LinkedContractNumber { get; init; }
    public int? LinkedActId { get; init; }
    public string? LinkedActNumber { get; init; }
    public DateTime? LinkedActDate { get; init; }
    public string? MatchStatus { get; init; }
    public DateTime? MatchedAt { get; init; }
    public string? MatchComment { get; init; }
    public int? AttachmentCount { get; init; }
    public string? AttachmentSummary { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public bool IsReadonly { get; init; }
    public int? RelatedPaymentId { get; init; }
    public int? CashOrderId { get; init; }
    public string? CashOrderNumber { get; init; }
    public DateTime? CashOrderDate { get; init; }
    public string? CashOrderStatus { get; init; }
    public bool CanCreateCashOrder { get; init; }
}
