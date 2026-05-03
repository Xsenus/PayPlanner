namespace PayPlanner.Api.Models.Responses;

public sealed class MoneyCategoryReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal NetTotal { get; init; }
    public IReadOnlyList<MoneyCategoryReportItemDto> Income { get; init; } = [];
    public IReadOnlyList<MoneyCategoryReportItemDto> Expense { get; init; } = [];
}

public sealed class MoneyCategoryReportItemDto
{
    public string CategoryName { get; init; } = string.Empty;
    public decimal Amount { get; init; }
    public int Count { get; init; }
    public decimal Share { get; init; }
}
