namespace PayPlanner.Api.Models.Responses;

public sealed class CashOrderDto
{
    public int Id { get; init; }
    public string Number { get; init; } = string.Empty;
    public CashOrderType Type { get; init; }
    public CashOrderStatus Status { get; init; }
    public DateTime Date { get; init; }
    public decimal Amount { get; init; }
    public string? Currency { get; init; }
    public int? ClientId { get; init; }
    public string? ClientName { get; init; }
    public int? PaymentId { get; init; }
    public int? MoneyOperationId { get; init; }
    public int? InvoiceId { get; init; }
    public string OriginType { get; init; } = "manual";
    public string? Account { get; init; }
    public string? PayerName { get; init; }
    public string? ReceiverName { get; init; }
    public string? Basis { get; init; }
    public string? Appendix { get; init; }
    public string? Comment { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancelledReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public int? CreatedByUserId { get; init; }
    public string? CreatedByUserName { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedByUserId { get; init; }
    public string? UpdatedByUserName { get; init; }
}
