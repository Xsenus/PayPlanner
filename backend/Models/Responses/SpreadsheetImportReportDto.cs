namespace PayPlanner.Api.Models.Responses;

public sealed class SpreadsheetImportReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int BatchCount { get; init; }
    public int FileCount { get; init; }
    public int TotalRows { get; init; }
    public int CreatedRows { get; init; }
    public int UpdatedRows { get; init; }
    public int SkippedRows { get; init; }
    public int ErrorRows { get; init; }
    public int RollbackRows { get; init; }
    public int AppliedCount { get; init; }
    public int AppliedWithErrorsCount { get; init; }
    public int RolledBackCount { get; init; }
    public int RolledBackWithConflictsCount { get; init; }
    public IReadOnlyList<SpreadsheetImportReportItemDto> Statuses { get; init; } = [];
}

public sealed class SpreadsheetImportReportItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int BatchCount { get; init; }
    public int FileCount { get; init; }
    public int TotalRows { get; init; }
    public int CreatedRows { get; init; }
    public int UpdatedRows { get; init; }
    public int SkippedRows { get; init; }
    public int ErrorRows { get; init; }
    public int RollbackRows { get; init; }
    public decimal Share { get; init; }
}
