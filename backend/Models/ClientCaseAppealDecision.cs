using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

/// <summary>
/// A recorded appeal choice for the court decision currently stored on a case.
/// Historical rows remain when a later decision supersedes the case's dates.
/// </summary>
public class ClientCaseAppealDecision
{
    public int Id { get; set; }

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    public DateTime CourtDecisionDate { get; set; }
    public DateTime AppealDeadlineDate { get; set; }

    [Required, MaxLength(24)]
    public string Choice { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Comment { get; set; } = string.Empty;

    public int? TaskJournalItemId { get; set; }
    public TaskJournalItem? TaskJournalItem { get; set; }

    public int? DecidedByUserId { get; set; }

    [MaxLength(256)]
    public string DecidedByUserName { get; set; } = string.Empty;

    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}
