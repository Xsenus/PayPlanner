namespace PayPlanner.Api.Models.Responses;

public sealed class YandexCalendarStartAuthResponseDto
{
    public string State { get; set; } = string.Empty;
    public string AuthUrl { get; set; } = string.Empty;
    public int PollIntervalMs { get; set; } = 2000;
}

public sealed class YandexCalendarAuthStatusDto
{
    public string State { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public string? Detail { get; set; }
    public string? ReturnUrl { get; set; }
}

public sealed class YandexCalendarStatusDto
{
    public bool IsAvailable { get; set; }
    public bool YandexOAuthReady { get; set; }
    public string CallbackUrl { get; set; } = string.Empty;
    public YandexCalendarConnectionDto? Connection { get; set; }
    public List<YandexCalendarSyncLogDto> RecentLogs { get; set; } = [];
}

public sealed class YandexCalendarConnectionDto
{
    public int Id { get; set; }
    public string? YandexEmail { get; set; }
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
    public int ManualFullSyncCooldownMinutes { get; set; }
    public DateTime? ManualFullSyncAvailableAt { get; set; }
    public DateTime? SyncFromDate { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<YandexCalendarCalendarDto> Calendars { get; set; } = [];
}

public sealed class YandexCalendarCalendarDto
{
    public int Id { get; set; }
    public int? OwnerUserId { get; set; }
    public string? OwnerName { get; set; }
    public string YandexCalendarId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime? LastSyncedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class YandexCalendarSyncLogDto
{
    public int Id { get; set; }
    public int? TaskJournalItemId { get; set; }
    public string Direction { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public sealed class YandexCalendarSyncNowResultDto
{
    public int ProcessedTasks { get; set; }
    public int ImportedEvents { get; set; }
    public int ErrorCount { get; set; }
    public string Message { get; set; } = string.Empty;
    public YandexCalendarStatusDto Status { get; set; } = new();
}
