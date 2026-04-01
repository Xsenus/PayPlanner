namespace PayPlanner.Api.Models.Requests;

public class UpdateDatabaseBackupSettingsRequest
{
    public bool AutomaticEnabled { get; init; }
    public string ScheduleLocal { get; init; } = string.Empty;
    public int RetentionDays { get; init; }
}
