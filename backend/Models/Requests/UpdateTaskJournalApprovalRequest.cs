namespace PayPlanner.Api.Models.Requests;

public class UpdateTaskJournalApprovalRequest
{
    public int? ParticipantId { get; set; }
    public string Decision { get; set; } = "approved";
    public string? Resolution { get; set; }
}
