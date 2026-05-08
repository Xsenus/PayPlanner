namespace PayPlanner.Api.Models.Requests;

public class UpdateTaskJournalStatusRequest
{
    public string Status { get; set; } = "new";
    public string? Result { get; set; }
}
