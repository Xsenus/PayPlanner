using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourtEvent
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    [Required, MaxLength(64)]
    public string EventType { get; set; } = "hearing";

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    public DateTime ScheduledAt { get; set; }

    public bool AllDay { get; set; }

    public DateTime? PreviousScheduledAt { get; set; }

    public bool? PreviousAllDay { get; set; }

    [Required, MaxLength(64)]
    public string CourtInstance { get; set; } = string.Empty;

    public int? CourtId { get; set; }
    public ClientCaseCourt? Court { get; set; }

    [MaxLength(200)]
    public string CourtName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string CourtCaseNumber { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Courtroom { get; set; } = string.Empty;

    [MaxLength(200)]
    public string JudgeName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string SecretaryName { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string MovementStatus { get; set; } = "scheduled";

    [MaxLength(1000)]
    public string ResultSummary { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? HearingOutcome { get; set; }

    [MaxLength(1000)]
    public string? HearingOutcomeNote { get; set; }

    public DateTime? HearingOutcomeRecordedAt { get; set; }

    public int? HearingOutcomeRecordedByUserId { get; set; }

    [MaxLength(256)]
    public string? HearingOutcomeRecordedByUserName { get; set; }

    [Required, MaxLength(64)]
    public string SourceType { get; set; } = "manual";

    [MaxLength(1000)]
    public string SourceUrl { get; set; } = string.Empty;

    public DateTime? SourceCheckedAt { get; set; }

    [Required, MaxLength(32)]
    public string SourceConfidence { get; set; } = "manual";

    [MaxLength(128)]
    public string? ExternalKey { get; set; }

    public int? ClientCaseDocumentId { get; set; }
    public ClientCaseDocument? ClientCaseDocument { get; set; }

    public int? ProceduralDeadlineId { get; set; }
    public ClientCaseProceduralDeadline? ProceduralDeadline { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;

    public ICollection<ClientCaseCourtEventHistory> History { get; set; } = new List<ClientCaseCourtEventHistory>();
}
