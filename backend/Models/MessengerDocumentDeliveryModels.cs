using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class MessengerDocumentChannels
{
    public const string Telegram = "telegram";
    public const string Max = "max";
    public const string WhatsAppBusiness = "whatsapp-business";
}

public static class MessengerDocumentDeliveryStatuses
{
    public const string Prepared = "prepared";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Sent = "sent";
    public const string Delivered = "delivered";
    public const string Read = "read";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public class MessengerRecipientBinding
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? ClientPortalAccountId { get; set; }
    public ClientPortalAccount? ClientPortalAccount { get; set; }

    [Required, MaxLength(32)] public string Channel { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string Provider { get; set; } = string.Empty;
    [Required, MaxLength(256)] public string DisplayName { get; set; } = string.Empty;
    [Required, MaxLength(512)] public string ProtectedAddress { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string AddressHash { get; set; } = string.Empty;
    [Required, MaxLength(128)] public string MaskedAddress { get; set; } = string.Empty;
    [MaxLength(128)] public string? ProviderRecipientId { get; set; }
    [Required, MaxLength(32)] public string VerificationStatus { get; set; } = "pending";
    [MaxLength(64)] public string? VerificationMethod { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public int? VerifiedByUserId { get; set; }
    public User? VerifiedByUser { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

public class MessengerDocumentDelivery
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int RecipientBindingId { get; set; }
    public MessengerRecipientBinding RecipientBinding { get; set; } = null!;
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? ClientCaseId { get; set; }
    public ClientCase? ClientCase { get; set; }

    [Required, MaxLength(32)] public string DocumentType { get; set; } = string.Empty;
    public int DocumentId { get; set; }
    public int DocumentVersion { get; set; } = 1;
    [Required, MaxLength(260)] public string OriginalFileName { get; set; } = string.Empty;
    [Required, MaxLength(600)] public string SnapshotPath { get; set; } = string.Empty;
    [Required, MaxLength(200)] public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    [Required, MaxLength(64)] public string ChecksumSha256 { get; set; } = string.Empty;

    [Required, MaxLength(32)] public string Channel { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string Provider { get; set; } = string.Empty;
    [Required, MaxLength(32)] public string Status { get; set; } = MessengerDocumentDeliveryStatuses.Prepared;
    [Required, MaxLength(128)] public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(256)] public string? ProviderMessageId { get; set; }
    [MaxLength(64)] public string? LinkTokenHash { get; set; }
    [MaxLength(512)] public string? ProtectedLinkToken { get; set; }
    public DateTime? LinkExpiresAt { get; set; }
    public DateTime? FirstOpenedAt { get; set; }
    public DateTime? LastOpenedAt { get; set; }
    public int DownloadCount { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    [MaxLength(2000)] public string? LastError { get; set; }
    [MaxLength(1000)] public string? MessagePreview { get; set; }
    public int CreatedByUserId { get; set; }
    public User CreatedByUser { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? QueuedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
