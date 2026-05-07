namespace PayPlanner.Api.Models.Responses;

public sealed class TransferPaymentRemainderResponse
{
    public Payment Payment { get; set; } = new();

    public Payment RemainderPayment { get; set; } = new();

    public bool CreatedRemainder { get; set; }
}
