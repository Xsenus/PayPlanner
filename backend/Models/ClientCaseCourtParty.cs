using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourtParty
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    public int? RelatedPartyId { get; set; }
    public ClientCaseRelatedParty? RelatedParty { get; set; }

    [Required, MaxLength(300)]
    public string DisplayName { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Side { get; set; } = "other";

    [Required, MaxLength(120)]
    public string ProcessRole { get; set; } = string.Empty;

    [MaxLength(300)]
    public string RepresentativeName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public bool IsArchived { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;
}
