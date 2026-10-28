namespace PayPlanner.Api.Models.Responses;

public sealed class CashOrderRegisterResponseDto
{
    public List<CashOrderDto> Items { get; init; } = [];
    public int Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int? MoneyAccountId { get; init; }
    public int IncomingCount { get; init; }
    public int OutgoingCount { get; init; }
    public int DraftCount { get; init; }
    public int PostedCount { get; init; }
    public int CancelledCount { get; init; }
    public bool AmountsAvailable { get; init; }
    public string CurrencyStatus { get; init; } = "empty";
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public string? Currency { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public decimal? IncomingAmount { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)]
    public decimal? OutgoingAmount { get; init; }
}
