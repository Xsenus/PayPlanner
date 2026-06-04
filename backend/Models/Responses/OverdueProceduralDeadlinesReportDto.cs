namespace PayPlanner.Api.Models.Responses;

public sealed class OverdueProceduralDeadlinesReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime AsOf { get; init; }
    public string DeadlineTypeFilter { get; init; } = string.Empty;
    public int? ResponsibleUserIdFilter { get; init; }
    public int Limit { get; init; }
    public int TotalOverdueCount { get; init; }
    public int CriticalOverdueCount { get; init; }
    public int WithoutResponsibleCount { get; init; }
    public IReadOnlyList<OverdueProceduralDeadlineTypeItemDto> ByType { get; init; } = [];
    public IReadOnlyList<OverdueProceduralDeadlineResponsibleItemDto> ByResponsible { get; init; } = [];
    public IReadOnlyList<OverdueProceduralDeadlineItemDto> Items { get; init; } = [];
}

public sealed class OverdueProceduralDeadlineTypeItemDto
{
    public string DeadlineType { get; init; } = string.Empty;
    public string DeadlineTypeLabel { get; init; } = string.Empty;
    public int Count { get; init; }
    public int CriticalCount { get; init; }
}

public sealed class OverdueProceduralDeadlineResponsibleItemDto
{
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public int Count { get; init; }
    public int CriticalCount { get; init; }
}

public sealed class OverdueProceduralDeadlineItemDto
{
    public int DeadlineId { get; init; }
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public string DeadlineType { get; init; } = string.Empty;
    public string DeadlineTypeLabel { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public DateTime DueAt { get; init; }
    public bool AllDay { get; init; }
    public int DaysOverdue { get; init; }
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int? TaskJournalItemId { get; init; }
    public string TaskTitle { get; init; } = string.Empty;
    public int? ClientCaseDocumentId { get; init; }
    public string DocumentName { get; init; } = string.Empty;
    public string SourceType { get; init; } = string.Empty;
    public int? SourceId { get; init; }
}
