using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseRelatedParty
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [MaxLength(32)]
    public string SourceType { get; set; } = string.Empty;

    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    public int? ExternalContactId { get; set; }
    public ExternalContact? ExternalContact { get; set; }

    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }

    [MaxLength(300)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Role { get; set; } = string.Empty;

    [MaxLength(500)]
    public string OrganizationName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;
}
