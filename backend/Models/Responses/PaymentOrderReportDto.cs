using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PaymentOrderReportDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public MoneyOperationSource? Source { get; init; }
    public decimal IncomeTotal { get; init; }
    public decimal ExpenseTotal { get; init; }
    public decimal NetTotal { get; init; }
    public IReadOnlyList<PaymentOrderReportItemDto> Orders { get; init; } = [];
}

public sealed class PaymentOrderReportItemDto
{
    public string PaymentOrderNumber { get; init; } = string.Empty;
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal NetTotal { get; init; }
    public decimal TotalAmount { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public decimal Share { get; init; }
}
