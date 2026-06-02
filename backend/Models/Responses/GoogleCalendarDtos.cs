namespace PayPlanner.Api.Models.Responses;

public sealed class GoogleCalendarStartAuthResponseDto
{
    public string State { get; set; } = string.Empty;
    public string AuthUrl { get; set; } = string.Empty;
    public int PollIntervalMs { get; set; } = 2000;
}

public sealed class GoogleCalendarAuthStatusDto
{
    public string State { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public string? Detail { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class GoogleCalendarStatusDto
{
    public bool IsAvailable { get; set; }
    public bool GoogleOAuthReady { get; set; }
    public string CallbackUrl { get; set; } = string.Empty;
    public GoogleCalendarConnectionDto? Connection { get; set; }
    public List<GoogleCalendarSyncLogDto> RecentLogs { get; set; } = [];
}

public sealed class GoogleCalendarConnectionDto
{
    public int Id { get; set; }
    public string? GoogleEmail { get; set; }
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; }
    public bool SyncEnabled { get; set; }
    public string CalendarMode { get; set; } = "personal";
    public string ConflictResolutionMode { get; set; } = "latestWins";
    public string? DefaultCalendarId { get; set; }
    public string? DefaultCalendarName { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public DateTime? LastFullSyncAt { get; set; }
    public DateTime? LastIncrementalSyncAt { get; set; }
    public DateTime? SyncFromDate { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<GoogleCalendarCalendarDto> Calendars { get; set; } = [];
}

public sealed class GoogleCalendarCalendarDto
{
    public int Id { get; set; }
    public int? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string GoogleCalendarId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public DateTime? WatchExpiresAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class GoogleCalendarSyncLogDto
{
    public int Id { get; set; }
    public int? TaskJournalItemId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class GoogleCalendarSyncNowResultDto
{
    public int ProcessedTasks { get; set; }
    public int ImportedEvents { get; set; }
    public int ErrorCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public GoogleCalendarStatusDto Status { get; set; } = new();
}
