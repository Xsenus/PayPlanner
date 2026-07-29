using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseDocument
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Required, MaxLength(64)]
    public string DocumentKey { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    [Required, MaxLength(120)]
    public string DocumentType { get; set; } = string.Empty;

    [Required, MaxLength(120)]
    public string Status { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string StoredFileName { get; set; } = string.Empty;

    [Required, MaxLength(600)]
    public string StoragePath { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    [Required, MaxLength(64)]
    public string SourceType { get; set; } = "manual";

    [MaxLength(1000)]
    public string SourceUrl { get; set; } = string.Empty;

    public DateTime? SourceCheckedAt { get; set; }

    [Required, MaxLength(32)]
    public string SourceConfidence { get; set; } = "manual";

    public int? UploadedByUserId { get; set; }
    public User? UploadedByUser { get; set; }

    [Required, MaxLength(256)]
    public string UploadedByName { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
