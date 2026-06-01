namespace PayPlanner.Api.Models.Requests;

public class UpdateDatabaseBackupSettingsRequest
{
    public bool AutomaticEnabled { get; init; }
    public string ScheduleLocal { get; init; } = string.Empty;
    public int RetentionDays { get; init; }
    public string? BackupFormat { get; init; }
    public bool? S3UploadEnabled { get; init; }
    public List<string>? S3ConnectionIds { get; init; }
    public string? S3PrefixTemplate { get; init; }
    public bool? YandexDiskUploadEnabled { get; init; }
    public string? YandexDiskPrefixTemplate { get; init; }
}
