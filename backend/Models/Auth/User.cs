using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models.Auth;

[Table("users")]
public class User
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    [Column("email")]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [Column("password_hash")]
    public string PasswordHash { get; set; } = string.Empty;

    [Column("full_name")]
    [MaxLength(255)]
    public string FullName { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = false; // Default to inactive, must be activated by admin

    [Column("is_activated")]
    public bool IsActivated { get; set; } = false; // Administrator activation flag

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public virtual UserProfile? Profile { get; set; }
}
