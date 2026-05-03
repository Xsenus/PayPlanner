using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class AdvanceReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal RecognizedTotal { get; init; }
    public decimal UnrecognizedTotal { get; init; }
    public int RecognizedCount { get; init; }
    public int UnrecognizedCount { get; init; }
    public IReadOnlyList<AdvanceReportItemDto> Counterparties { get; init; } = [];
}

public sealed class AdvanceReportItemDto
{
    public string CounterpartyName { get; init; } = string.Empty;
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal RecognizedTotal { get; init; }
    public decimal UnrecognizedTotal { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int RecognizedCount { get; init; }
    public int UnrecognizedCount { get; init; }
}
