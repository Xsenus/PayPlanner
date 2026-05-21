using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseProceduralDeadline
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Required, MaxLength(64)]
    public string DeadlineType { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public DateTime DueAt { get; set; }

    public bool AllDay { get; set; } = true;

    [Required, MaxLength(32)]
    public string Status { get; set; } = "active";

    public int WarningDaysBefore { get; set; } = 7;

    public int? TaskJournalItemId { get; set; }
    public TaskJournalItem? TaskJournalItem { get; set; }

    public int? ClientCaseDocumentId { get; set; }
    public ClientCaseDocument? ClientCaseDocument { get; set; }

    [MaxLength(64)]
    public string SourceType { get; set; } = "manual";

    public int? SourceId { get; set; }

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;

    public DateTime? ClosedAt { get; set; }

    public int? ClosedByUserId { get; set; }

    [MaxLength(256)]
    public string ClosedByUserName { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string CloseReason { get; set; } = string.Empty;
}
