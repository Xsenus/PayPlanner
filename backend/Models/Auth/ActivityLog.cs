using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models.Auth;

[Table("activity_logs")]
public class ActivityLog
{
    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Required]
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [Column("action_type")]
    [MaxLength(50)]
    public string ActionType { get; set; } = string.Empty; // login, logout, view, create, update, delete, click

    [Required]
    [Column("section")]
    [MaxLength(100)]
    public string Section { get; set; } = string.Empty; // calendar, clients, users, etc.

    [Column("details")]
    public string? Details { get; set; } // JSON with detailed information

    [Column("ip_address")]
    [MaxLength(50)]
    public string? IpAddress { get; set; }

    [Column("user_agent")]
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    [Column("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public virtual User User { get; set; } = null!;
}
