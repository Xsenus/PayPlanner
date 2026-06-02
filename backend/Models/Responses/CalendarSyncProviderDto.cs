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

public sealed class CalendarSyncReadinessDto
{
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public bool ExternalCalendarSyncEnabled { get; set; }
    public bool ReadyToConnect { get; set; }
    public List<CalendarSyncReadinessItemDto> Items { get; set; } = [];
}

public sealed class CalendarSyncReadinessItemDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsRequired { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class CalendarSyncSettingsDto
{
    public string SyncMode { get; set; } = "notConfigured";
    public string ConflictResolutionMode { get; set; } = "manualConfirmation";
    public string UserControlMode { get; set; } = "userManaged";
    public DateTime? DefaultSyncFromDate { get; set; }
    public string InactiveUserAction { get; set; } = "cleanupExternalData";
    public bool RequireManualConfirmation { get; set; } = true;
    public bool DryRunConfirmed { get; set; }
    public string Notes { get; set; } = string.Empty;
    public DateTime? UpdatedAt { get; set; }
}

public sealed class CalendarSyncLogEntryDto
{
    public string Provider { get; set; } = string.Empty;
    public string ProviderLabel { get; set; } = string.Empty;
    public int Id { get; set; }
    public int? ConnectionId { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public int? TaskJournalItemId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class CalendarSyncLogPageDto
{
    public List<CalendarSyncLogEntryDto> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
