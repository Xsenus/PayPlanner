using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class YandexCalendarAuthRequest
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    [Required]
    [MaxLength(128)]
    public string State { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string ReturnUrl { get; set; } = "/";

    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = "pending";

    [MaxLength(2000)]
    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
