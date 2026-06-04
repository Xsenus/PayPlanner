namespace PayPlanner.Api.Models.Responses;

public sealed class CurrentCasesReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime AsOf { get; init; }
    public string StatusFilter { get; init; } = string.Empty;
    public int? ResponsibleUserIdFilter { get; init; }
    public string CaseTypeFilter { get; init; } = string.Empty;
    public int Limit { get; init; }
    public int TotalCurrentCases { get; init; }
    public int OpenCases { get; init; }
    public int OnHoldCases { get; init; }
    public int OverdueCases { get; init; }
    public int WithoutResponsibleCount { get; init; }
    public int WithOpenTasksCount { get; init; }
    public int WithOverdueTasksCount { get; init; }
    public int WithOverdueDeadlinesCount { get; init; }
    public IReadOnlyList<CurrentCasesStatusItemDto> ByStatus { get; init; } = [];
    public IReadOnlyList<CurrentCasesResponsibleItemDto> ByResponsible { get; init; } = [];
    public IReadOnlyList<CurrentCaseReportItemDto> Items { get; init; } = [];
}

public sealed class CurrentCasesStatusItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int Count { get; init; }
    public int OverdueCases { get; init; }
    public int OpenTasks { get; init; }
}

public sealed class CurrentCasesResponsibleItemDto
{
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public int CaseCount { get; init; }
    public int OverdueCases { get; init; }
    public int OpenTasks { get; init; }
    public int OverdueTasks { get; init; }
    public int OverdueDeadlines { get; init; }
}

public sealed class CurrentCaseReportItemDto
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
    public DateTime? NextHearingAt { get; init; }
    public int DaysInWork { get; init; }
    public int? DaysUntilDue { get; init; }
    public bool IsOverdue { get; init; }
    public int OpenTaskCount { get; init; }
    public int OverdueTaskCount { get; init; }
    public int OnApprovalTaskCount { get; init; }
    public int ActiveDeadlineCount { get; init; }
    public int OverdueDeadlineCount { get; init; }
    public DateTime? NextDeadlineAt { get; init; }
    public bool NextDeadlineAllDay { get; init; }
    public string NextDeadlineLabel { get; init; } = string.Empty;
    public DateTime LastActivityAt { get; init; }
}
