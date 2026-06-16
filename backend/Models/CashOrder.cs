namespace PayPlanner.Api.Models;

public class CashOrder
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public CashOrderType Type { get; set; }
    public CashOrderStatus Status { get; set; } = CashOrderStatus.Posted;
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? Currency { get; set; } = "RUB";
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public int? MoneyOperationId { get; set; }
    public MoneyOperation? MoneyOperation { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public string? Account { get; set; }
    public string? PayerName { get; set; }
    public string? ReceiverName { get; set; }
    public string? Basis { get; set; }
    public string? Appendix { get; set; }
    public string? Comment { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }
    public string? UpdatedByUserName { get; set; }
}
