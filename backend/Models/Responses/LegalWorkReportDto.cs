namespace PayPlanner.Api.Models.Responses;

public sealed class LegalWorkReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string StatusFilter { get; init; } = string.Empty;
    public int? ResponsibleUserIdFilter { get; init; }
    public string CaseTypeFilter { get; init; } = string.Empty;
    public bool HasCompletePaymentVisibility { get; init; }
    public int TotalCases { get; init; }
    public int ActiveCases { get; init; }
    public int ClosedCases { get; init; }
    public int LostCases { get; init; }
    public int OnHoldCases { get; init; }
    public int OverdueDeadlineCount { get; init; }
    public int UpcomingDeadlineCount { get; init; }
    public int OpenTaskCount { get; init; }
    public int OverdueTaskCount { get; init; }
    public int OnApprovalTaskCount { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public IReadOnlyList<LegalWorkCaseStatusItemDto> CasesByStatus { get; init; } = [];
    public IReadOnlyList<LegalWorkCaseTypeItemDto> CasesByType { get; init; } = [];
    public IReadOnlyList<LegalWorkResponsibleItemDto> Responsibles { get; init; } = [];
    public IReadOnlyList<LegalWorkFinanceItemDto> FinanceByStatus { get; init; } = [];
    public IReadOnlyList<LegalWorkDeadlineSummaryItemDto> DeadlinesByType { get; init; } = [];
    public IReadOnlyList<LegalWorkCaseItemDto> Cases { get; init; } = [];
    public IReadOnlyList<LegalWorkDeadlineItemDto> OverdueDeadlines { get; init; } = [];
    public IReadOnlyList<LegalWorkDeadlineItemDto> UpcomingDeadlines { get; init; } = [];
}

public sealed class LegalWorkCaseStatusItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal FeeTotal { get; init; }
    public int OverdueDeadlines { get; init; }
}

public sealed class LegalWorkCaseTypeItemDto
{
    public string CaseType { get; init; } = string.Empty;
    public int Count { get; init; }
    public decimal FeeTotal { get; init; }
    public int ActiveTasks { get; init; }
}

public sealed class LegalWorkResponsibleItemDto
{
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public int ActiveCases { get; init; }
    public int ClosedCases { get; init; }
    public int LostCases { get; init; }
    public int OpenTasks { get; init; }
    public int OverdueTasks { get; init; }
    public int OnApprovalTasks { get; init; }
    public int OverdueDeadlines { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
}

public sealed class LegalWorkFinanceItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int CaseCount { get; init; }
    public decimal FeeTotal { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
}

public sealed class LegalWorkDeadlineSummaryItemDto
{
    public string DeadlineType { get; init; } = string.Empty;
    public string DeadlineLabel { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int OverdueCount { get; init; }
    public int UpcomingCount { get; init; }
}

public sealed class LegalWorkCaseItemDto
{
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string CaseType { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? DueDate { get; init; }
    public decimal FeeAmount { get; init; }
    public decimal PaidTotal { get; init; }
    public decimal OutstandingTotal { get; init; }
    public int OpenTasks { get; init; }
    public int OverdueTasks { get; init; }
    public int OnApprovalTasks { get; init; }
    public int OverdueDeadlines { get; init; }
    public int UpcomingDeadlines { get; init; }
}

public sealed class LegalWorkDeadlineItemDto
{
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public string DeadlineType { get; init; } = string.Empty;
    public string DeadlineLabel { get; init; } = string.Empty;
    public DateTime DueAt { get; init; }
    public int DaysOffset { get; init; }
}
