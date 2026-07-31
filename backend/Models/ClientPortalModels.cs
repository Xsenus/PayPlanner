using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public sealed class ClientPortalAccount
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    [MaxLength(320)] public string Email { get; set; } = string.Empty;
    [MaxLength(256)] public string DisplayName { get; set; } = string.Empty;
    [MaxLength(32)] public string AccountType { get; set; } = "client";
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    [MaxLength(256)] public string CreatedByUserName { get; set; } = string.Empty;
    public DateTime? RevokedAt { get; set; }
    public int? RevokedByUserId { get; set; }
    [MaxLength(256)] public string RevokedByUserName { get; set; } = string.Empty;
    [MaxLength(500)] public string RevocationReason { get; set; } = string.Empty;
    public ICollection<ClientPortalCaseGrant> CaseGrants { get; set; } = [];
    public ICollection<ClientPortalSession> Sessions { get; set; } = [];
}

public sealed class ClientPortalCaseGrant
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int ClientPortalAccountId { get; set; }
    public ClientPortalAccount Account { get; set; } = null!;
    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;
    public bool CanViewCaseSummary { get; set; }
    public bool CanViewDeadlines { get; set; }
    public bool CanViewDocuments { get; set; }
    public bool CanViewFinance { get; set; }
    public bool CanViewApprovals { get; set; }
    public bool CanViewMessages { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
    public int? RevokedByUserId { get; set; }
    [MaxLength(500)] public string RevocationReason { get; set; } = string.Empty;
}

public sealed class ClientPortalDocumentGrant
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int ClientPortalCaseGrantId { get; set; }
    public ClientPortalCaseGrant CaseGrant { get; set; } = null!;
    public int ClientCaseDocumentId { get; set; }
    public ClientCaseDocument Document { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class ClientPortalLoginChallenge
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int ClientPortalAccountId { get; set; }
    public ClientPortalAccount Account { get; set; } = null!;
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    [MaxLength(64)] public string OtpHash { get; set; } = string.Empty;
    public int FailedAttempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

public sealed class ClientPortalSession
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public int ClientPortalAccountId { get; set; }
    public ClientPortalAccount Account { get; set; } = null!;
    [MaxLength(64)] public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; }
    [MaxLength(64)] public string IpHash { get; set; } = string.Empty;
    [MaxLength(512)] public string UserAgent { get; set; } = string.Empty;
}

public sealed class ClientPortalAuditEvent
{
    public long Id { get; set; }
    public int? ClientPortalAccountId { get; set; }
    public ClientPortalAccount? Account { get; set; }
    public int? ClientPortalCaseGrantId { get; set; }
    [MaxLength(64)] public string EventType { get; set; } = string.Empty;
    [MaxLength(64)] public string TargetType { get; set; } = string.Empty;
    [MaxLength(128)] public string TargetKey { get; set; } = string.Empty;
    [MaxLength(64)] public string IpHash { get; set; } = string.Empty;
    [MaxLength(512)] public string UserAgent { get; set; } = string.Empty;
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
