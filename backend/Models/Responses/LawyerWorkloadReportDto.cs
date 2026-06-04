namespace PayPlanner.Api.Models.Responses;

public sealed class LawyerWorkloadReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime AsOf { get; init; }
    public int HorizonDays { get; init; }
    public int? LawyerUserIdFilter { get; init; }
    public int Limit { get; init; }
    public int LawyerCount { get; init; }
    public int TotalActiveCases { get; init; }
    public int TotalOpenTasks { get; init; }
    public int TotalOverdueTasks { get; init; }
    public int TotalOverdueDeadlines { get; init; }
    public int OverloadedLawyerCount { get; init; }
    public decimal TotalPlannedHours { get; init; }
    public IReadOnlyList<LawyerWorkloadItemDto> Items { get; init; } = [];
}

public sealed class LawyerWorkloadItemDto
{
    public int? LawyerUserId { get; init; }
    public string LawyerName { get; init; } = string.Empty;
    public int ActiveCaseCount { get; init; }
    public int OpenTaskCount { get; init; }
    public int OverdueTaskCount { get; init; }
    public int OnApprovalTaskCount { get; init; }
    public int DueTodayTaskCount { get; init; }
    public int DueSoonTaskCount { get; init; }
    public int OverdueDeadlineCount { get; init; }
    public int ActiveDeadlineCount { get; init; }
    public int PlannedMinutes { get; init; }
    public decimal PlannedHours { get; init; }
    public int WorkloadScore { get; init; }
    public bool IsOverloaded { get; init; }
    public string RiskLabel { get; init; } = string.Empty;
    public DateTime? NearestTaskDueAt { get; init; }
    public DateTime? NearestDeadlineAt { get; init; }
    public bool NearestDeadlineAllDay { get; init; }
}
