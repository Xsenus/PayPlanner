using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class MaxRecipientLinkStatuses
{
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string OptedOut = "opted-out";
    public const string Blocked = "blocked";
}

public sealed class MaxRecipientLink
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int? UserId { get; set; }
    public User? User { get; set; }
    public int? ClientPortalAccountId { get; set; }
    public ClientPortalAccount? ClientPortalAccount { get; set; }
    public long? MaxUserId { get; set; }
    public long? MaxChatId { get; set; }
    [MaxLength(32)] public string Status { get; set; } = MaxRecipientLinkStatuses.Pending;
    [MaxLength(64)] public string LinkTokenHash { get; set; } = string.Empty;
    public DateTime LinkTokenExpiresAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? OptedOutAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class MaxInboundEvent
{
    public int Id { get; set; }
    [MaxLength(64)] public string Fingerprint { get; set; } = string.Empty;
    [MaxLength(64)] public string UpdateType { get; set; } = string.Empty;
    public long? MaxUserId { get; set; }
    public long? MaxChatId { get; set; }
    [MaxLength(256)] public string? ExternalMessageId { get; set; }
    [MaxLength(256)] public string? CallbackId { get; set; }
    [MaxLength(64)] public string PayloadHash { get; set; } = string.Empty;
    [MaxLength(32)] public string ProcessingStatus { get; set; } = "recorded";
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
