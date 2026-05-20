namespace PayPlanner.Api.Models.Responses;

public sealed class CaseActivityReportDto
{
    public DateTime GeneratedAt { get; init; }
    public int InactivityDays { get; init; }
    public int EscalationDays { get; init; }
    public string Activity { get; init; } = "all";
    public int TotalVisibleCases { get; init; }
    public int ActiveCaseCount { get; init; }
    public int FinishedCaseCount { get; init; }
    public int InactiveCaseCount { get; init; }
    public int EscalatedCaseCount { get; init; }
    public int DisabledCaseCount { get; init; }
    public int WithActiveTasksCount { get; init; }
    public int WithoutResponsibleCount { get; init; }
    public IReadOnlyList<CaseActivityReportItemDto> Items { get; init; } = [];
}

public sealed class CaseActivityReportItemDto
{
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string CaseType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int? OwnerId { get; init; }
    public string OwnerType { get; init; } = string.Empty;
    public string OwnerName { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; init; }
    public DateTime? LastTaskAt { get; init; }
    public DateTime? LastViewedAt { get; init; }
    public DateTime? ActivityConfirmedAt { get; init; }
    public int DaysWithoutActivity { get; init; }
    public int ActiveTaskCount { get; init; }
    public int CompletedTaskCount { get; init; }
    public bool InactivityRemindersEnabled { get; init; }
    public bool IsInactive { get; init; }
    public bool IsEscalated { get; init; }
    public string Reason { get; init; } = string.Empty;
}
