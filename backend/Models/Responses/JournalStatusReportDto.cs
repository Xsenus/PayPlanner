using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class JournalStatusReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal NetTotal { get; init; }
    public int OperationCount { get; init; }
    public int PostedCount { get; init; }
    public int ProcessingCount { get; init; }
    public int ErrorCount { get; init; }
    public IReadOnlyList<JournalStatusReportItemDto> Statuses { get; init; } = [];
}

public sealed class JournalStatusReportItemDto
{
    public MoneyOperationJournalStatus Status { get; init; }
    public string StatusLabel { get; init; } = string.Empty;
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetTotal { get; init; }
    public decimal TotalAmount { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int OperationCount { get; init; }
    public decimal Share { get; init; }
}
