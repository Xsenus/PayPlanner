namespace PayPlanner.Api.Models.Responses;

public sealed class CaseSuccessReportDto
{
    public DateTime GeneratedAt { get; init; }
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string StatusFilter { get; init; } = string.Empty;
    public int? ResponsibleUserIdFilter { get; init; }
    public string CaseTypeFilter { get; init; } = string.Empty;
    public int Limit { get; init; }
    public int TotalCases { get; init; }
    public int CompletedCases { get; init; }
    public int SuccessfulCases { get; init; }
    public int LostCases { get; init; }
    public int InProgressCases { get; init; }
    public int OnHoldCases { get; init; }
    public decimal SuccessRate { get; init; }
    public decimal LossRate { get; init; }
    public decimal CompletionRate { get; init; }
    public string FormulaNote { get; init; } = string.Empty;
    public IReadOnlyList<CaseSuccessStatusItemDto> ByStatus { get; init; } = [];
    public IReadOnlyList<CaseSuccessResponsibleItemDto> ByResponsible { get; init; } = [];
    public IReadOnlyList<CaseSuccessTypeItemDto> ByType { get; init; } = [];
    public IReadOnlyList<CaseSuccessItemDto> Items { get; init; } = [];
}

public sealed class CaseSuccessStatusItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int CaseCount { get; init; }
    public decimal Share { get; init; }
}

public sealed class CaseSuccessResponsibleItemDto
{
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public int TotalCases { get; init; }
    public int CompletedCases { get; init; }
    public int SuccessfulCases { get; init; }
    public int LostCases { get; init; }
    public int InProgressCases { get; init; }
    public decimal SuccessRate { get; init; }
}

public sealed class CaseSuccessTypeItemDto
{
    public string CaseType { get; init; } = string.Empty;
    public int TotalCases { get; init; }
    public int CompletedCases { get; init; }
    public int SuccessfulCases { get; init; }
    public int LostCases { get; init; }
    public decimal SuccessRate { get; init; }
}

public sealed class CaseSuccessItemDto
{
    public int CaseId { get; init; }
    public string CaseNumber { get; init; } = string.Empty;
    public string CaseTitle { get; init; } = string.Empty;
    public string ClientName { get; init; } = string.Empty;
    public string CaseType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public string OutcomeCategory { get; init; } = string.Empty;
    public string OutcomeLabel { get; init; } = string.Empty;
    public int? ResponsibleUserId { get; init; }
    public string ResponsibleName { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? DueDate { get; init; }
    public int DaysInWork { get; init; }
    public bool IsFinished { get; init; }
}
