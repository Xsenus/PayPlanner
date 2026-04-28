using System;

namespace PayPlanner.Api.Models;

public enum ClientBalanceTransactionType
{
    Overpayment,
    CreditApplied,
    ManualAdjustment,
    Refund
}

public enum ClientBalanceDirection
{
    Credit,
    Debit
}

public class ClientBalanceTransaction
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public Client? Client { get; set; }
    public int? InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }
    public int? PaymentId { get; set; }
    public Payment? Payment { get; set; }
    public decimal Amount { get; set; }
    public ClientBalanceTransactionType Type { get; set; }
    public ClientBalanceDirection Direction { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ExternalKey { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
}
