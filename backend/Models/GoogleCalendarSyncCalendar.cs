using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class GoogleCalendarSyncCalendar
{
    public int Id { get; set; }

    public int ConnectionId { get; set; }
    public GoogleCalendarConnection Connection { get; set; } = null!;

    public int? OwnerUserId { get; set; }
    public User? OwnerUser { get; set; }

    [Required]
    [MaxLength(256)]
    public string GoogleCalendarId { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Name { get; set; } = string.Empty;

    public string? SyncToken { get; set; }

    [MaxLength(128)]
    public string? WatchChannelId { get; set; }

    [MaxLength(256)]
    public string? WatchResourceId { get; set; }

    [MaxLength(128)]
    public string? WatchToken { get; set; }

    public DateTime? WatchExpiresAt { get; set; }
    public DateTime? LastSyncedAt { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<GoogleCalendarTaskLink> TaskLinks { get; set; } = new List<GoogleCalendarTaskLink>();
}
