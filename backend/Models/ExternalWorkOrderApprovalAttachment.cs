using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public sealed class ExternalWorkOrderApprovalAttachment
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int ExternalWorkOrderApprovalRequestId { get; set; }
    public ExternalWorkOrderApprovalRequest Request { get; set; } = null!;

    public int ClientCaseDocumentId { get; set; }
    public ClientCaseDocument Document { get; set; } = null!;

    [MaxLength(260)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;
}
