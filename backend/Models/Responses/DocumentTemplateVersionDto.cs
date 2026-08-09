namespace PayPlanner.Api.Models.Responses;

public class DocumentTemplateVersionDto
{
    public int Id { get; set; }
    public int Revision { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string ActiveTemplateKey { get; set; } = string.Empty;
    public List<string> TemplateNames { get; set; } = new();
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? RestoredFromVersionId { get; set; }
}
