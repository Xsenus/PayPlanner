namespace PayPlanner.Api.Models;

public class NotificationSender
{
    public int Id { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public string PurposesJson { get; set; } = "[]";
    public string ConfigJson { get; set; } = "{}";
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
