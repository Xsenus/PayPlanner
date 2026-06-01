namespace PayPlanner.Api.Models;

public class YandexDiskCrmSyncState
{
    public int Id { get; set; }
    public string DocumentKind { get; set; } = string.Empty;
    public string ObjectType { get; set; } = string.Empty;
    public int ObjectId { get; set; }
    public int? ParentObjectId { get; set; }
    public string? FileName { get; set; }
    public string? YandexDiskPath { get; set; }
    public string Status { get; set; } = YandexDiskCrmSyncStatus.Pending;
    public string? LastError { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class YandexDiskCrmSyncStatus
{
    public const string Pending = "pending";
    public const string Success = "success";
    public const string Failed = "failed";
}
