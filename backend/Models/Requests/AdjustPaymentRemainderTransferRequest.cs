namespace PayPlanner.Api.Models.Requests;

public sealed class AdjustPaymentRemainderTransferRequest
{
    public decimal PaidAmount { get; set; }
    public DateTime RemainderDate { get; set; }
}
