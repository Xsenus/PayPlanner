using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Person (Individual Contact) - split from legacy Client table
/// Can be linked to a Company or standalone
/// </summary>
[Table("persons")]
public class Person
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("first_name")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [Column("last_name")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Column("middle_name")]
    [MaxLength(100)]
    public string? MiddleName { get; set; }

    [Column("company_id")]
    public int? CompanyId { get; set; }

    [Column("position")]
    [MaxLength(200)]
    public string? Position { get; set; }

    [Column("phone")]
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Column("email")]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [Column("address")]
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [Column("notes")]
    [MaxLength(2000)]
    public string Notes { get; set; } = string.Empty;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("legacy_client_id")]
    public int? LegacyClientId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CompanyId")]
    public virtual Company? Company { get; set; }

    public virtual ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();
}
