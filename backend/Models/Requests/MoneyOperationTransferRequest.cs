namespace PayPlanner.Api.Models.Requests;

public sealed class MoneyOperationTransferRequest
{
    public int SourceMoneyAccountId { get; init; }
    public int TargetMoneyAccountId { get; init; }
    public decimal Amount { get; init; }
    public string? Currency { get; init; }
    public DateTime Date { get; init; }
    public DateTime? RecognitionDate { get; init; }
    public string? PaymentOrderNumber { get; init; }
    public string? Purpose { get; init; }
    public int? ResponsibleUserId { get; init; }
}
