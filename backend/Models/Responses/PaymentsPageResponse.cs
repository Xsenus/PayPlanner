using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class PaymentsListSummaryDto
{
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal Balance { get; init; }
    public int IncomeCount { get; init; }
    public int ExpenseCount { get; init; }
    public int Total { get; init; }
}

public sealed class PaymentsPageResponse : PagedResponse<Payment>
{
    public PaymentsListSummaryDto Summary { get; init; } = new();
}
