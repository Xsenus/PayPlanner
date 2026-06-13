using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class CashFlowReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public string? AccountName { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetCashFlow { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public IReadOnlyList<CashFlowSourceBreakdownDto> BySource { get; init; } = [];
    public IReadOnlyList<CashFlowAccountBreakdownDto> ByAccount { get; init; } = [];
    public IReadOnlyList<CashFlowDayDto> Days { get; init; } = [];
}

public sealed class CashFlowSourceBreakdownDto
{
    public MoneyOperationSource Source { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetCashFlow { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}

public sealed class CashFlowAccountBreakdownDto
{
    public MoneyOperationSource Source { get; init; }
    public string AccountName { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetCashFlow { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}

public sealed class CashFlowDayDto
{
    public DateTime Date { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetCashFlow { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}

public sealed class CashFlowForecastReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public string? AccountName { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal ExpectedIncome { get; init; }
    public decimal ExpectedExpense { get; init; }
    public decimal NetProjectedFlow { get; init; }
    public decimal ProjectedClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public IReadOnlyList<CashFlowForecastDayDto> Days { get; init; } = [];
}

public sealed class CashFlowForecastDayDto
{
    public DateTime Date { get; init; }
    public decimal ExpectedIncome { get; init; }
    public decimal ExpectedExpense { get; init; }
    public decimal NetProjectedFlow { get; init; }
    public decimal ProjectedClosingBalance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
}
