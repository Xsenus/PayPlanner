namespace PayPlanner.Api.Models.Responses;

public class ClientCaseDocumentDto
{
    public int Id { get; set; }
    public int ClientCaseId { get; set; }
    public string DocumentKey { get; set; } = string.Empty;
    public int? VersionGroupId { get; set; }
    public string? TemplateKey { get; set; }
    public string? TemplateLabel { get; set; }
    public int? SourceTaskId { get; set; }
    public int? SourceEventId { get; set; }
    public int? SourceWorkOrderId { get; set; }
    public int? SourceInvoiceId { get; set; }
    public string? SourceStage { get; set; }
    public int Version { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceUrl { get; set; } = string.Empty;
    public DateTime? SourceCheckedAt { get; set; }
    public string SourceConfidence { get; set; } = string.Empty;
    public int? UploadedByUserId { get; set; }
    public string UploadedByName { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public bool IsLatestVersion { get; set; }
}
