namespace PayPlanner.Api.Models;

public enum BalanceApplicationState { Committed, Cancelled }

// Immutable outcome fence. Scalar IDs deliberately do not change historical deletion/cascade rules.
public sealed class BalanceApplicationReceipt
{
    public int ActorUserId { get; set; }
    public Guid RequestId { get; set; }
    public string? CommandSha256 { get; set; }
    public BalanceApplicationState State { get; set; } = BalanceApplicationState.Committed;
    public string CommandVersion { get; set; } = "balance-apply-v1";
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public int PaymentId { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal Balance { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
