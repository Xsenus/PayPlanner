namespace PayPlanner.Api.Models.Requests;

public class UpsertExternalContactRequest
{
    public string FullName { get; set; } = string.Empty;
    public string ContactType { get; set; } = "Person";
    public string? Inn { get; set; }
    public List<string> Roles { get; set; } = new();
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public List<string> Phones { get; set; } = new();
    public List<string> Emails { get; set; } = new();
    public string? Position { get; set; }
    public string? PowerOfAttorney { get; set; }
    public string? Notes { get; set; }
    public int? LegalEntityId { get; set; }
    public int? ParentContactId { get; set; }
    public int? ResponsibleUserId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool SyncToPhonebook { get; set; } = true;
}
