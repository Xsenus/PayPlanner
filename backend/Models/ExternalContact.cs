using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ExternalContact
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string ContactType { get; set; } = "Person";

    [MaxLength(20)]
    public string? Inn { get; set; }

    [MaxLength(2000)]
    public string? RolesJson { get; set; }

    [MaxLength(100)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    [EmailAddress]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Position { get; set; }

    [MaxLength(500)]
    public string? PowerOfAttorney { get; set; }

    [MaxLength(4000)]
    public string? PhonesJson { get; set; }

    [MaxLength(4000)]
    public string? EmailsJson { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }

    public int? ParentContactId { get; set; }
    public ExternalContact? ParentContact { get; set; }
    public ICollection<ExternalContact> Employees { get; set; } = new List<ExternalContact>();

    public int? LinkedClientId { get; set; }
    public Client? LinkedClient { get; set; }

    public int? LinkedLegalEntityId { get; set; }
    public LegalEntity? LinkedLegalEntity { get; set; }

    public int? ResponsibleUserId { get; set; }
    public User? ResponsibleUser { get; set; }

    public bool IsActive { get; set; } = true;
    public bool SyncToPhonebook { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
