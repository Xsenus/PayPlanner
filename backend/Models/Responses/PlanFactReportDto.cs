using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PlanFactReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal PlanIncome { get; init; }
    public decimal FactIncome { get; init; }
    public decimal IncomeVariance { get; init; }
    public decimal IncomeCompletionPercent { get; init; }
    public decimal PlanExpense { get; init; }
    public decimal FactExpense { get; init; }
    public decimal ExpenseVariance { get; init; }
    public decimal ExpenseExecutionPercent { get; init; }
    public decimal PlanNetTotal { get; init; }
    public decimal FactNetTotal { get; init; }
    public decimal NetVariance { get; init; }
    public IReadOnlyList<PlanFactDayDto> Days { get; init; } = [];
}

public sealed class PlanFactDayDto
{
    public DateTime Date { get; init; }
    public decimal PlanIncome { get; init; }
    public decimal FactIncome { get; init; }
    public decimal IncomeVariance { get; init; }
    public decimal PlanExpense { get; init; }
    public decimal FactExpense { get; init; }
    public decimal ExpenseVariance { get; init; }
    public decimal PlanNetTotal { get; init; }
    public decimal FactNetTotal { get; init; }
    public decimal NetVariance { get; init; }
}
