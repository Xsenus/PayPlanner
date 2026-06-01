namespace PayPlanner.Api.Models.Requests;

public sealed class StartGoogleCalendarAuthRequest
{
    public string? ReturnUrl { get; set; }
}

public sealed class UpdateGoogleCalendarSettingsRequest
{
    public bool? IsEnabled { get; set; }
    public bool? SyncEnabled { get; set; }
    public string? CalendarMode { get; set; }
    public string? ConflictResolutionMode { get; set; }
}
