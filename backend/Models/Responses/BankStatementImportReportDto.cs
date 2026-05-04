namespace PayPlanner.Api.Models.Responses;

public sealed class BankStatementImportReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public int BatchCount { get; init; }
    public int RowsReadTotal { get; init; }
    public int CreatedRowsTotal { get; init; }
    public int SkippedRowsTotal { get; init; }
    public int ErrorRowsTotal { get; init; }
    public int RollbackRowsTotal { get; init; }
    public int AppliedCount { get; init; }
    public int AppliedWithErrorsCount { get; init; }
    public int RolledBackCount { get; init; }
    public int RollbackBlockedCount { get; init; }
    public IReadOnlyList<BankStatementImportReportItemDto> Statuses { get; init; } = [];
}

public sealed class BankStatementImportReportItemDto
{
    public string Status { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public int BatchCount { get; init; }
    public int RowsRead { get; init; }
    public int CreatedRows { get; init; }
    public int SkippedRows { get; init; }
    public int ErrorRows { get; init; }
    public int RollbackRows { get; init; }
    public decimal Share { get; init; }
}
