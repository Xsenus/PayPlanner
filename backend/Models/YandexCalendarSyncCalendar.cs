using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class YandexCalendarSyncCalendar
{
    public int Id { get; set; }

    public int ConnectionId { get; set; }
    public YandexCalendarConnection Connection { get; set; } = null!;

    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    [Required]
    [MaxLength(512)]
    public string YandexCalendarId { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string CalendarUrl { get; set; } = string.Empty;

    public string? SyncToken { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<YandexCalendarTaskLink> TaskLinks { get; set; } = new List<YandexCalendarTaskLink>();
}
