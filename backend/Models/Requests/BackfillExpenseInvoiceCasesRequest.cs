namespace PayPlanner.Api.Models.Requests;

public sealed class BackfillExpenseInvoiceCasesRequest
{
    public bool DryRun { get; set; } = true;

    public bool Confirm { get; set; }

    public int? Limit { get; set; }

    public List<int>? PaymentIds { get; set; }
}
