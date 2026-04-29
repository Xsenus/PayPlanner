namespace PayPlanner.Api.Models.Responses;

public sealed class CashBookResponseDto
{
    public DateTime From { get; init; }
    public DateTime To { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomingCount { get; init; }
    public int OutgoingCount { get; init; }
    public List<CashBookDayDto> Days { get; init; } = [];
}

public sealed class CashBookDayDto
{
    public DateTime Date { get; init; }
    public decimal OpeningBalance { get; init; }
    public decimal Income { get; init; }
    public decimal Expense { get; init; }
    public decimal ClosingBalance { get; init; }
    public int IncomingCount { get; init; }
    public int OutgoingCount { get; init; }
}
