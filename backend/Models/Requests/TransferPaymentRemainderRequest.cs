using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public sealed class TransferPaymentRemainderRequest
{
    public Payment Payment { get; set; } = new();

    public DateTime RemainderDate { get; set; }
}
