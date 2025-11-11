using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

public class UserActivityLog
{
    public long Id { get; set; }

    public int? UserId { get; set; }

    [MaxLength(256)]
    public string? UserEmail { get; set; }

    [MaxLength(256)]
    public string? UserFullName { get; set; }

    [MaxLength(128)]
    public string Category { get; set; } = string.Empty;

    [MaxLength(160)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(160)]
    public string? Section { get; set; }

    [MaxLength(160)]
    public string? ObjectType { get; set; }

    [MaxLength(160)]
    public string? ObjectId { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    public UserActivityStatus Status { get; set; } = UserActivityStatus.Info;

    [MaxLength(64)]
    public string? IpAddress { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    [MaxLength(16)]
    public string? HttpMethod { get; set; }

    [MaxLength(512)]
    public string? Path { get; set; }

    [MaxLength(512)]
    public string? QueryString { get; set; }

    public int? HttpStatusCode { get; set; }

    public long? DurationMs { get; set; }

    [Column(TypeName = "text")]
    public string? MetadataJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
