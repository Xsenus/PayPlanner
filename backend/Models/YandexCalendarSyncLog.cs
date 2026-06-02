using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class YandexCalendarSyncLog
{
    public int Id { get; set; }

    public int? ConnectionId { get; set; }
    public YandexCalendarConnection? Connection { get; set; }

    public int? CalendarSyncCalendarId { get; set; }
    public YandexCalendarSyncCalendar? CalendarSyncCalendar { get; set; }

    public int? TaskJournalItemId { get; set; }
    public TaskJournalItem? TaskJournalItem { get; set; }

    [Required]
    [MaxLength(32)]
    public string Direction { get; set; } = "system";

    [Required]
    [MaxLength(64)]
    public string Operation { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "success";

    [Required]
    [MaxLength(1000)]
    public string Message { get; set; } = string.Empty;

    public string? DetailsJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
