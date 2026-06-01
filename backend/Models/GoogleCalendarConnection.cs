using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class GoogleCalendarConnection
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    [MaxLength(128)]
    public string? GoogleUserId { get; set; }

    [MaxLength(320)]
    public string? GoogleEmail { get; set; }

    [MaxLength(256)]
    public string? DisplayName { get; set; }

    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }

    [MaxLength(1000)]
    public string Scope { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;
    public bool SyncEnabled { get; set; } = true;

    [MaxLength(32)]
    public string CalendarMode { get; set; } = "personal";

    [MaxLength(32)]
    public string ConflictResolutionMode { get; set; } = "latestWins";

    [MaxLength(256)]
    public string? DefaultCalendarId { get; set; }

    [MaxLength(256)]
    public string? DefaultCalendarName { get; set; }

    public DateTime? LastFullSyncAt { get; set; }
    public DateTime? LastIncrementalSyncAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GoogleCalendarSyncCalendar> Calendars { get; set; } = new List<GoogleCalendarSyncCalendar>();
    public ICollection<GoogleCalendarTaskLink> TaskLinks { get; set; } = new List<GoogleCalendarTaskLink>();
}
