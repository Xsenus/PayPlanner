using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class RollbackPaymentRemainderTransferResponse
{
    public Payment Payment { get; set; } = new();
    public int RemovedRemainderPaymentId { get; set; }
}
