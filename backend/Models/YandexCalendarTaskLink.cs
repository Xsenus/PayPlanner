using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class YandexCalendarTaskLink
{
    public int Id { get; set; }

    public int ConnectionId { get; set; }
    public YandexCalendarConnection Connection { get; set; } = null!;

    public int CalendarSyncCalendarId { get; set; }
    public YandexCalendarSyncCalendar CalendarSyncCalendar { get; set; } = null!;

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    [Required]
    [MaxLength(512)]
    public string YandexCalendarId { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string YandexEventId { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? YandexEventUrl { get; set; }

    [MaxLength(256)]
    public string? YandexEtag { get; set; }

    [MaxLength(128)]
    public string? LastCrmHash { get; set; }

    [MaxLength(128)]
    public string? LastYandexHash { get; set; }

    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(32)]
    public string? LastDirection { get; set; }

    [MaxLength(32)]
    public string SyncStatus { get; set; } = "active";

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
