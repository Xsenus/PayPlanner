using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class MoneyOperationListItemDto
{
    public int Id { get; init; }
    public MoneyOperationSource Source { get; init; }
    public MoneyOperationDirection Direction { get; init; }
    public MoneyOperationAccountingType AccountingType { get; init; }
    public MoneyOperationJournalStatus JournalStatus { get; init; }
    public int? MoneyAccountId { get; init; }
    public string? MoneyAccountName { get; init; }
    public string? AccountName { get; init; }
    public string? CounterpartyName { get; init; }
    public int? ResponsibleUserId { get; init; }
    public string? ResponsibleName { get; init; }
    public int? IncomeTypeId { get; init; }
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
    public int? LinkedInvoiceId { get; init; }
    public string? LinkedInvoiceNumber { get; init; }
    public DateTime? LinkedInvoiceDate { get; init; }
    public int? LinkedContractId { get; init; }
    public string? LinkedContractNumber { get; init; }
    public MoneyOperationMatchStatus MatchStatus { get; init; }
    public DateTime? MatchedAt { get; init; }
    public string? MatchComment { get; init; }
    public int AttachmentCount { get; init; }
    public string? AttachmentSummary { get; init; }
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

public sealed class MoneyOperationAccountSummaryDto
{
    public string AccountName { get; init; } = string.Empty;
    public MoneyOperationSource Source { get; init; }
    public decimal StartBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal EndBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}

public sealed class MoneyOperationDailyBalanceDto
{
    public DateTime Date { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public MoneyOperationSource Source { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public bool IsBalanced { get; init; } = true;
}

public sealed class MoneyBalanceReconciliationResultDto
{
    public int Id { get; init; }
    public DateTime Date { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public MoneyOperationSource Source { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public bool IsBalanced { get; init; } = true;
    public int IssueCount { get; init; }
    public DateTime CheckedAt { get; init; }
    public int? CheckedByUserId { get; init; }
}

public sealed class MoneyBalanceReconciliationRunResultDto
{
    public DateTime CheckedAt { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int IssueCount { get; init; }
    public IReadOnlyList<MoneyBalanceReconciliationResultDto> Items { get; init; } =
        Array.Empty<MoneyBalanceReconciliationResultDto>();
}

public sealed class MoneyOperationInvoiceCandidateDto
{
    public int InvoiceId { get; init; }
    public int PaymentId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime? InvoiceDate { get; init; }
    public decimal Amount { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal OutstandingAmount { get; init; }
    public PaymentStatus Status { get; init; }
    public int? ClientId { get; init; }
    public string? ClientName { get; init; }
    public string? CounterpartyName { get; init; }
    public string? MatchReason { get; init; }
    public int Score { get; init; }
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
