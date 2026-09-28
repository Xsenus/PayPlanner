using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class DatabaseBackupAttemptStatuses
{
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Degraded = "degraded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Interrupted = "interrupted";
}

public sealed class DatabaseBackupAttempt
{
    [MaxLength(64)] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [MaxLength(64)] public string RootIdentity { get; set; } = string.Empty;
    [MaxLength(32)] public string Trigger { get; set; } = string.Empty;
    [MaxLength(64)] public string? ScheduleSlot { get; set; }
    [MaxLength(32)] public string Status { get; set; } = DatabaseBackupAttemptStatuses.Running;
    [MaxLength(32)] public string Stage { get; set; } = "preparing";
    [MaxLength(64)] public string? BackupSetId { get; set; }
    public DatabaseBackupSet? BackupSet { get; set; }
    [MaxLength(2000)] public string? Error { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public static class DatabaseBackupSetStatuses
{
    public const string LocalVerified = "localVerified";
    public const string Replicating = "replicating";
    public const string AwaitingCopies = "awaitingCopies";
    public const string VerifiedConfigured = "verifiedConfigured";
    public const string Degraded = "degraded";
    public const string NoLocalCopy = "noLocalCopy";
}

public static class DatabaseBackupCopyStatuses
{
    public const string Pending = "pending";
    public const string Verified = "verified";
    public const string Failed = "failed";
    public const string Deleted = "deleted";
}

public sealed class DatabaseBackupSet
{
    [MaxLength(64)] public string Id { get; set; } = string.Empty;
    [MaxLength(2048)] public string BackupRelativePath { get; set; } = string.Empty;
    [MaxLength(64)] public string DatabaseProvider { get; set; } = string.Empty;
    [MaxLength(32)] public string BackupFormat { get; set; } = string.Empty;
    [MaxLength(64)] public string ManifestSha256 { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = DatabaseBackupSetStatuses.LocalVerified;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<DatabaseBackupCopy> Copies { get; set; } = new List<DatabaseBackupCopy>();
}

public sealed class DatabaseBackupCopy
{
    public long Id { get; set; }
    [MaxLength(64)] public string BackupSetId { get; set; } = string.Empty;
    public DatabaseBackupSet BackupSet { get; set; } = null!;
    [MaxLength(32)] public string ProviderKey { get; set; } = string.Empty;
    [MaxLength(128)] public string ConnectionId { get; set; } = string.Empty;
    [MaxLength(2048)] public string Locator { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = DatabaseBackupCopyStatuses.Pending;
    public int AttemptCount { get; set; }
    [MaxLength(2000)] public string? LastError { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
