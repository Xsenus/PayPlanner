namespace PayPlanner.Api.Models;

public enum PaymentAdvanceReviewStatus
{
    None,
    RequiresDecision,
    AcceptedAsAdvance,
    Allocated,
    Ignored,
    Refunded
}
