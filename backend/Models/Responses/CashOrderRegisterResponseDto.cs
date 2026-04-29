namespace PayPlanner.Api.Models.Responses;

public sealed class CashOrderRegisterResponseDto
{
    public List<CashOrderDto> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int IncomingCount { get; init; }
    public int OutgoingCount { get; init; }
    public int PostedCount { get; init; }
    public int CancelledCount { get; init; }
    public decimal IncomingAmount { get; init; }
    public decimal OutgoingAmount { get; init; }
}
