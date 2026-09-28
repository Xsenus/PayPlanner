using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

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
