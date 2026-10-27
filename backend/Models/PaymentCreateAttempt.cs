using PayPlanner.Api.Services.Payments;

namespace PayPlanner.Api.Models;

/// <summary>Private ownership/outcome metadata; contains no financial draft payload.</summary>
public sealed class PaymentCreateAttempt
{
    public const string Draft = "Draft";
    public const string Committed = "Committed";
    public const string Cancelled = "Cancelled";
    public Guid Id { get; set; }
    public int ActorUserId { get; set; }
    public string Operation { get; set; } = "primary";
    public int? SellerCompanyId { get; set; }
    public int? SellerBranchId { get; set; }
    public int? PrimaryPaymentId { get; set; }
    public string State { get; set; } = Draft;
    public string IdentityVersion { get; set; } = PaymentCreateCommand.Version;
    public string? PayloadIdentity { get; set; }
    public int? CanonicalPaymentId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public DateTimeOffset? CancelledAtUtc { get; set; }
}
