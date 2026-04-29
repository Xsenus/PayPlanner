using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class MoneyOperationListItemDto
{
    public int Id { get; init; }
    public MoneyOperationSource Source { get; init; }
    public MoneyOperationDirection Direction { get; init; }
    public MoneyOperationAccountingType AccountingType { get; init; }
    public string? AccountName { get; init; }
    public string? CounterpartyName { get; init; }
    public string? CategoryName { get; init; }
    public decimal Amount { get; init; }
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
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public bool IsReadonly { get; init; }
    public int? RelatedPaymentId { get; init; }
    public int? CashOrderId { get; init; }
    public string? CashOrderNumber { get; init; }
    public CashOrderStatus? CashOrderStatus { get; init; }
    public bool CanCreateCashOrder { get; init; }
}

public sealed class MoneyOperationSourceSummaryDto
{
    public MoneyOperationSource Source { get; init; }
    public decimal StartBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal EndBalance { get; init; }
}

public sealed class MoneyOperationsSummaryDto
{
    public decimal StartBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal EndBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public IReadOnlyList<MoneyOperationSourceSummaryDto> BySource { get; init; } = Array.Empty<MoneyOperationSourceSummaryDto>();
}

public sealed class MoneyOperationsPageResponse : PagedResponse<MoneyOperationListItemDto>
{
    public MoneyOperationsSummaryDto Summary { get; init; } = new();
}
