namespace PayPlanner.Api.Models;

/// <summary>
/// Структурная связь исходного частично оплаченного платежа с созданным платежом-остатком.
/// </summary>
public sealed class PaymentRemainderTransfer
{
    public int Id { get; set; }

    public int SourcePaymentId { get; set; }
    public Payment? SourcePayment { get; set; }

    public int RemainderPaymentId { get; set; }
    public Payment? RemainderPayment { get; set; }

    public decimal SourceOriginalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal RemainderAmount { get; set; }
    public DateTime RemainderDate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
