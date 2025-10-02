using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models.Auth;

[Table("user_profiles")]
public class UserProfile
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [Column("last_name")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Required]
    [Column("first_name")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Column("patronymic")]
    [MaxLength(100)]
    public string? Patronymic { get; set; }

    [Column("date_of_birth")]
    public DateTime? DateOfBirth { get; set; }

    [Column("hire_date")]
    public DateTime? HireDate { get; set; }

    [Column("termination_date")]
    public DateTime? TerminationDate { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("UserId")]
    public virtual User User { get; set; } = null!;

    public virtual ICollection<UserContact> Contacts { get; set; } = new List<UserContact>();
    public virtual ICollection<UserSocialProfile> SocialProfiles { get; set; } = new List<UserSocialProfile>();
}

[Table("user_contacts")]
public class UserContact
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("profile_id")]
    public int ProfileId { get; set; }

    [Required]
    [Column("contact_type")]
    [MaxLength(50)]
    public string ContactType { get; set; } = string.Empty; // phone, email

    [Required]
    [Column("contact_value")]
    [MaxLength(255)]
    public string ContactValue { get; set; } = string.Empty;

    [Column("is_primary")]
    public bool IsPrimary { get; set; } = false;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("ProfileId")]
    public virtual UserProfile Profile { get; set; } = null!;
}

[Table("user_social_profiles")]
public class UserSocialProfile
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("profile_id")]
    public int ProfileId { get; set; }

    [Required]
    [Column("platform")]
    [MaxLength(50)]
    public string Platform { get; set; } = string.Empty; // facebook, telegram, whatsapp, etc.

    [Required]
    [Column("profile_url")]
    [MaxLength(500)]
    public string ProfileUrl { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("ProfileId")]
    public virtual UserProfile Profile { get; set; } = null!;
}
