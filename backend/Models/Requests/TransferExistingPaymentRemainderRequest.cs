namespace PayPlanner.Api.Models.Requests;

public sealed class TransferExistingPaymentRemainderRequest
{
    public DateTime RemainderDate { get; set; }
    public decimal? OriginalAmount { get; set; }
    public decimal? ExpectedAmount { get; set; }
    public decimal? ExpectedPaidAmount { get; set; }
    public string? ConfirmationToken { get; set; }
}
