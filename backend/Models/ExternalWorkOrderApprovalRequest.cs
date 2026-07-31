using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public sealed class ExternalWorkOrderApprovalRequest
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ClientCaseWorkOrderId { get; set; }
    public ClientCaseWorkOrder WorkOrder { get; set; } = null!;

    public int Version { get; set; }

    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    public string SnapshotJson { get; set; } = string.Empty;

    [MaxLength(64)]
    public string SnapshotHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string AccessTokenHash { get; set; } = string.Empty;

    [MaxLength(64)]
    public string OtpHash { get; set; } = string.Empty;

    [MaxLength(256)]
    public string RecipientName { get; set; } = string.Empty;

    [MaxLength(320)]
    public string RecipientEmail { get; set; } = string.Empty;

    [MaxLength(32)]
    public string RecipientType { get; set; } = "client";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? OpenedAt { get; set; }
    public DateTime? LastSentAt { get; set; }
    public int FailedAttempts { get; set; }
    public int MaxAttempts { get; set; } = 5;

    [MaxLength(32)]
    public string Decision { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string DecisionComment { get; set; } = string.Empty;

    public DateTime? DecisionAt { get; set; }

    [MaxLength(64)]
    public string DecisionIpHash { get; set; } = string.Empty;

    [MaxLength(512)]
    public string DecisionUserAgent { get; set; } = string.Empty;

    public DateTime? RevokedAt { get; set; }
    public int? RevokedByUserId { get; set; }

    [MaxLength(256)]
    public string RevokedByUserName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string RevocationReason { get; set; } = string.Empty;
}
