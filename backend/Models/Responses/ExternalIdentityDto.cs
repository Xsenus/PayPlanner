namespace PayPlanner.Api.Models.Responses;

public class ExternalIdentityDto
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderUserId { get; set; } = string.Empty;
    public string? ProviderEmail { get; set; }
    public string? ProviderUsername { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string? TelegramUserId { get; set; }
    public long? ChatId { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
