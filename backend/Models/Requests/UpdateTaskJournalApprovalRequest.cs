namespace PayPlanner.Api.Models.Requests;

public class UpdateTaskJournalApprovalRequest
{
    public int? ParticipantId { get; set; }
    public string Decision { get; set; } = "approved";
    public string? FinalAction { get; set; }
    public string? Resolution { get; set; }
    public DateTime? RevisionDueAt { get; set; }
}
