namespace PayPlanner.Api.Models.Responses;

public class ClientCaseRelatedPartyDto
{
    public int Id { get; set; }
    public int ClientCaseId { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public int? SourceId { get; set; }
    public int? ClientId { get; set; }
    public int? ExternalContactId { get; set; }
    public int? LegalEntityId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public string CreatedByUserName { get; set; } = string.Empty;
    public bool IsArchived { get; set; }
    public DateTime? ArchivedAt { get; set; }
    public int? ArchivedByUserId { get; set; }
    public string ArchivedByUserName { get; set; } = string.Empty;
    public string ArchiveReason { get; set; } = string.Empty;
}
