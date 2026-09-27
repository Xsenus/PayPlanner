namespace PayPlanner.Api.Models.Responses;

public sealed class CaseFinanceReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string StatusFilter { get; init; } = string.Empty;
    public int? ResponsibleUserIdFilter { get; init; }
    public string CaseTypeFilter { get; init; } = string.Empty;
    public int Limit { get; init; }
    public bool HasCompletePaymentVisibility { get; init; }
    public int TotalCases { get; init; }
    public int ActiveCases { get; init; }
    public int ClosedCases { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public decimal MarginTotal { get; init; }
    public decimal CollectionRate { get; init; }
    public decimal MarginRate { get; init; }
    public int FullyPaidCaseCount { get; init; }
    public int PartiallyPaidCaseCount { get; init; }
    public int UnpaidCaseCount { get; init; }
    public int OverpaidCaseCount { get; init; }
    public IReadOnlyList<CaseFinanceStatusItemDto> ByStatus { get; init; } = [];
    public IReadOnlyList<CaseFinanceResponsibleItemDto> ByResponsible { get; init; } = [];
    public IReadOnlyList<CaseFinanceItemDto> Items { get; init; } = [];
}

public sealed class CaseFinanceStatusItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int CaseCount { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public decimal MarginTotal { get; init; }
    public decimal CollectionRate { get; init; }
    public decimal MarginRate { get; init; }
}

public sealed class CaseFinanceResponsibleItemDto
{
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public int CaseCount { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public decimal MarginTotal { get; init; }
    public decimal CollectionRate { get; init; }
    public decimal MarginRate { get; init; }
}

public sealed class CaseFinanceItemDto
{
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string CaseType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? DueDate { get; init; }
    public decimal FeeAmount { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public decimal MarginTotal { get; init; }
    public decimal PaymentRate { get; init; }
    public decimal MarginRate { get; init; }
    public string FinanceState { get; init; } = string.Empty;
    public string FinanceStateLabel { get; init; } = string.Empty;
}
