namespace PayPlanner.Api.Models.Requests;

public sealed class RepairInvoicePaymentStateRequest
{
    public bool DryRun { get; set; } = true;

    public int? Limit { get; set; }

    public List<int>? InvoiceIds { get; set; }

    public List<int>? PaymentIds { get; set; }
}
