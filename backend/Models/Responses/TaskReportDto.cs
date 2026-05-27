namespace PayPlanner.Api.Models.Responses;

public sealed class TaskReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime AsOf { get; init; }
    public string Scope { get; init; } = "open";
    public int? AssigneeUserIdFilter { get; init; }
    public int Limit { get; init; }
    public int TotalTaskCount { get; init; }
    public int OpenTaskCount { get; init; }
    public int ClosedTaskCount { get; init; }
    public int OverdueTaskCount { get; init; }
    public int DueTodayTaskCount { get; init; }
    public int WithoutAssigneeCount { get; init; }
    public IReadOnlyList<TaskReportStatusItemDto> ByStatus { get; init; } = [];
    public IReadOnlyList<TaskReportResponsibleItemDto> ByResponsible { get; init; } = [];
    public IReadOnlyList<TaskReportItemDto> Items { get; init; } = [];
}

public sealed class TaskReportStatusItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int Count { get; init; }
    public int OverdueCount { get; init; }
    public int Share { get; init; }
}

public sealed class TaskReportResponsibleItemDto
{
    public int? AssigneeUserId { get; init; }
    public string AssigneeName { get; init; } = string.Empty;
    public int TotalTasks { get; init; }
    public int OpenTasks { get; init; }
    public int ClosedTasks { get; init; }
    public int OverdueTasks { get; init; }
    public DateTime? NearestDueAt { get; init; }
}

public sealed class TaskReportItemDto
{
    public int TaskId { get; init; }
    public int? ClientId { get; init; }
    public string ClientName { get; init; } = string.Empty;
    public int? LegalEntityId { get; init; }
    public string LegalEntityName { get; init; } = string.Empty;
    public int? ClientCaseId { get; init; }
    public string ClientCaseTitle { get; init; } = string.Empty;
    public string ObjectName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int? AssigneeUserId { get; init; }
    public string AssigneeName { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTime DueAt { get; init; }
    public int PlannedDurationMinutes { get; init; }
    public string Type { get; init; } = string.Empty;
    public string TypeLabel { get; init; } = string.Empty;
    public string Priority { get; init; } = string.Empty;
    public string PriorityLabel { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string Result { get; init; } = string.Empty;
    public bool IsClosed { get; init; }
    public bool IsOverdue { get; init; }
    public int? DaysOverdue { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
