namespace PayPlanner.Api.Models;

public class AppRelease
{
    public int Id { get; set; }
    public string ReleaseId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTime ReleasedAt { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string Source { get; set; } = "manual";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public int? UpdatedByUserId { get; set; }
    public string? UpdatedByUserName { get; set; }

    public ICollection<AppReleaseItem> Items { get; set; } = new List<AppReleaseItem>();
    public ICollection<AppReleaseSeen> SeenByUsers { get; set; } = new List<AppReleaseSeen>();
}

public class AppReleaseItem
{
    public int Id { get; set; }
    public int AppReleaseId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    public AppRelease? AppRelease { get; set; }
}

public class AppReleaseSeen
{
    public int Id { get; set; }
    public int AppReleaseId { get; set; }
    public int UserId { get; set; }
    public DateTime SeenAt { get; set; } = DateTime.UtcNow;

    public AppRelease? AppRelease { get; set; }
    public User? User { get; set; }
}
