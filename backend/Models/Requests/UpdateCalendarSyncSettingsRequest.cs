namespace PayPlanner.Api.Models.Requests;

public sealed class UpdateCalendarSyncSettingsRequest
{
    public bool? ExternalCalendarSyncEnabled { get; set; }
    public bool? GoogleCalendarSyncEnabled { get; set; }
    public bool? YandexCalendarSyncEnabled { get; set; }
    public int? GoogleSyncIntervalMinutes { get; set; }
    public int? YandexSyncIntervalMinutes { get; set; }
    public string? SyncMode { get; set; }
    public string? ConflictResolutionMode { get; set; }
    public string? UserControlMode { get; set; }
    public DateTime? DefaultSyncFromDate { get; set; }
    public string? InactiveUserAction { get; set; }
    public bool? RequireManualConfirmation { get; set; }
    public bool? DryRunConfirmed { get; set; }
    public string? Notes { get; set; }
}
