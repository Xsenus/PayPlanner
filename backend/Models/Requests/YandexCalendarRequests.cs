namespace PayPlanner.Api.Models.Requests;

public sealed class StartYandexCalendarAuthRequest
{
    public string? ReturnUrl { get; set; }
}

public sealed class UpdateYandexCalendarSettingsRequest
{
    public bool? IsEnabled { get; set; }
    public bool? SyncEnabled { get; set; }
    public string? CalendarMode { get; set; }
    public string? ConflictResolutionMode { get; set; }
    public DateTime? SyncFromDate { get; set; }
}
