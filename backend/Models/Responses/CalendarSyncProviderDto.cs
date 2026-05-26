namespace PayPlanner.Api.Models.Responses;

public sealed class CalendarSyncProviderDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsAvailable { get; set; }
    public bool FeatureFlagEnabled { get; set; }
    public bool RequiresOAuth { get; set; }
    public bool SupportsManualExport { get; set; }
    public bool SupportsBackgroundSync { get; set; }
    public bool SupportsTwoWaySync { get; set; }
    public string[] SupportedTargets { get; set; } = [];
    public string Message { get; set; } = string.Empty;
}
