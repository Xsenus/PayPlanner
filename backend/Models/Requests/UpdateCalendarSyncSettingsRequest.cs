namespace PayPlanner.Api.Models.Requests;

public sealed class UpdateCalendarSyncSettingsRequest
{
    public string? SyncMode { get; set; }
    public string? ConflictResolutionMode { get; set; }
    public bool? RequireManualConfirmation { get; set; }
    public bool? DryRunConfirmed { get; set; }
    public string? Notes { get; set; }
}
