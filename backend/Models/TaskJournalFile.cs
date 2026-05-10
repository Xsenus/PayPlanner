using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalFile
{
    public int Id { get; set; }

    public int TaskJournalItemId { get; set; }
    public TaskJournalItem TaskJournalItem { get; set; } = null!;

    [Required, MaxLength(64)]
    public string DocumentKey { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    [Required, MaxLength(260)]
    public string OriginalFileName { get; set; } = string.Empty;

    [Required, MaxLength(260)]
    public string StoredFileName { get; set; } = string.Empty;

    [Required, MaxLength(600)]
    public string StoragePath { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    public long SizeBytes { get; set; }

    public int? UploadedByUserId { get; set; }
    public User? UploadedByUser { get; set; }

    [Required, MaxLength(256)]
    public string UploadedByName { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
