namespace PayPlanner.Api.Models.Responses;

public class ContactDirectoryItemDto
{
    public string SourceType { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int SourceId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string? OrganizationName { get; set; }
    public int? LegalEntityId { get; set; }
    public string? Position { get; set; }
    public string? PowerOfAttorney { get; set; }
    public string? Notes { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public List<string> Phones { get; set; } = new();
    public List<string> Emails { get; set; } = new();
    public bool IsActive { get; set; }
    public bool SyncToPhonebook { get; set; }
    public int? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
