namespace PayPlanner.Api.Models;

public class ExternalAuthRequest
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string ReturnUrl { get; set; } = "/";
    public string Status { get; set; } = "pending";
    public string? Error { get; set; }
    public int? UserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    public User? User { get; set; }
}
