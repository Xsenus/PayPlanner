using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class ElectronicSignatureTypes
{
    public const string Simple = "simple";
    public const string EnhancedUnqualified = "enhanced-unqualified";
    public const string Qualified = "qualified";
}

public static class ElectronicSignatureSigningOrders
{
    public const string Sequential = "sequential";
    public const string Parallel = "parallel";
}

public static class ElectronicSignatureEnvelopeStatuses
{
    public const string Draft = "draft";
    public const string Prepared = "prepared";
    public const string Sending = "sending";
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Declined = "declined";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string VerificationFailed = "verification-failed";
}

public sealed class ElectronicSignatureEnvelope
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    [MaxLength(32)] public string DocumentType { get; set; } = string.Empty;
    public int DocumentId { get; set; }
    public int DocumentVersion { get; set; } = 1;
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    [MaxLength(32)] public string SignatureType { get; set; } = ElectronicSignatureTypes.Qualified;
    [MaxLength(32)] public string SigningOrder { get; set; } = ElectronicSignatureSigningOrders.Sequential;
    [MaxLength(64)] public string Provider { get; set; } = string.Empty;
    [MaxLength(256)] public string? ProviderEnvelopeId { get; set; }
    [MaxLength(32)] public string Status { get; set; } = ElectronicSignatureEnvelopeStatuses.Draft;
    [MaxLength(260)] public string OriginalFileName { get; set; } = string.Empty;
    [MaxLength(1024)] public string SnapshotPath { get; set; } = string.Empty;
    [MaxLength(128)] public string ContentType { get; set; } = "application/pdf";
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string ChecksumSha256 { get; set; } = string.Empty;
    [MaxLength(64)] public string IdempotencyKey { get; set; } = string.Empty;
    public int ArchiveRetentionYears { get; set; } = 10;
    public bool RequiresMachineReadablePowerOfAttorney { get; set; }
    public int CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    [MaxLength(2000)] public string? LastError { get; set; }
    public ICollection<ElectronicSignatureSigner> Signers { get; set; } = new List<ElectronicSignatureSigner>();
    public ICollection<ElectronicSignatureArtifact> Artifacts { get; set; } = new List<ElectronicSignatureArtifact>();
    public ICollection<ElectronicSignatureEvent> Events { get; set; } = new List<ElectronicSignatureEvent>();
}

public sealed class ElectronicSignatureSigner
{
    public int Id { get; set; }
    public int EnvelopeId { get; set; }
    public ElectronicSignatureEnvelope Envelope { get; set; } = null!;
    public int Order { get; set; }
    [MaxLength(32)] public string PartyType { get; set; } = "client";
    public int? ClientPortalAccountId { get; set; }
    public ClientPortalAccount? ClientPortalAccount { get; set; }
    [MaxLength(256)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(512)] public string ProtectedContact { get; set; } = string.Empty;
    [MaxLength(256)] public string MaskedContact { get; set; } = string.Empty;
    [MaxLength(256)] public string? ProviderSignerId { get; set; }
    [MaxLength(32)] public string Status { get; set; } = "pending";
    public bool RequiresMachineReadablePowerOfAttorney { get; set; }
    [MaxLength(256)] public string? PowerOfAttorneyReference { get; set; }
    [MaxLength(256)] public string? CertificateThumbprint { get; set; }
    [MaxLength(512)] public string? CertificateSubject { get; set; }
    [MaxLength(512)] public string? CertificateIssuer { get; set; }
    public DateTime? CertificateValidFrom { get; set; }
    public DateTime? CertificateValidTo { get; set; }
    [MaxLength(64)] public string? CertificateRevocationStatus { get; set; }
    public DateTime? InvitedAt { get; set; }
    public DateTime? SignedAt { get; set; }
    public DateTime? DeclinedAt { get; set; }
    [MaxLength(2000)] public string? DeclineReason { get; set; }
}

public sealed class ElectronicSignatureArtifact
{
    public int Id { get; set; }
    public int EnvelopeId { get; set; }
    public ElectronicSignatureEnvelope Envelope { get; set; } = null!;
    public int? SignerId { get; set; }
    public ElectronicSignatureSigner? Signer { get; set; }
    [MaxLength(32)] public string ArtifactType { get; set; } = string.Empty;
    [MaxLength(1024)] public string StoragePath { get; set; } = string.Empty;
    [MaxLength(260)] public string FileName { get; set; } = string.Empty;
    [MaxLength(128)] public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string ChecksumSha256 { get; set; } = string.Empty;
    [MaxLength(32)] public string VerificationStatus { get; set; } = "pending";
    public DateTime? VerifiedAt { get; set; }
    [MaxLength(2000)] public string? VerificationDetails { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class ElectronicSignatureEvent
{
    public int Id { get; set; }
    public int EnvelopeId { get; set; }
    public ElectronicSignatureEnvelope Envelope { get; set; } = null!;
    [MaxLength(256)] public string? ProviderEventId { get; set; }
    [MaxLength(64)] public string EventType { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = string.Empty;
    [MaxLength(2000)] public string Message { get; set; } = string.Empty;
    [MaxLength(64)] public string? PayloadHash { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
