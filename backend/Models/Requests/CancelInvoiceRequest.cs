using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public enum InvoiceCancellationMoneyAction
{
    Reject,
    LeaveAsAdvance,
    Refund,
    Reallocate
}

public enum InvoiceCancellationScope
{
    All,
    UnpaidInstallmentsOnly
}

public class CancelInvoiceRequest
{
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;

    public InvoiceCancellationScope CancellationScope { get; set; } = InvoiceCancellationScope.All;

    public InvoiceCancellationMoneyAction ReceivedMoneyAction { get; set; } = InvoiceCancellationMoneyAction.Reject;

    public int? TargetPaymentId { get; set; }

    public MoneyOperationSource? RefundSource { get; set; }

    public DateTime? RefundDate { get; set; }
}
