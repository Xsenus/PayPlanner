using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Company (Legal Entity) - split from legacy Client table
/// Non-destructive addition - original Clients table preserved
/// </summary>
[Table("companies")]
public class Company
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("legal_name")]
    [MaxLength(300)]
    public string LegalName { get; set; } = string.Empty;

    [Column("registration_number")]
    [MaxLength(100)]
    public string? RegistrationNumber { get; set; }

    [Column("tax_id")]
    [MaxLength(100)]
    public string? TaxId { get; set; }

    [Column("legal_address")]
    [MaxLength(500)]
    public string LegalAddress { get; set; } = string.Empty;

    [Column("phone")]
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Column("email")]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

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

    public virtual ICollection<Person> Persons { get; set; } = new List<Person>();
    public virtual ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();
}
