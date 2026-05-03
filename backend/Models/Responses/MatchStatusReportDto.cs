using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class MatchStatusReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal NetTotal { get; init; }
    public int OperationCount { get; init; }
    public int MatchedCount { get; init; }
    public int NeedsAttentionCount { get; init; }
    public IReadOnlyList<MatchStatusReportItemDto> Statuses { get; init; } = [];
}

public sealed class MatchStatusReportItemDto
{
    public MoneyOperationMatchStatus Status { get; init; }
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
