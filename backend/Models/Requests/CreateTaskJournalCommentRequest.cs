namespace PayPlanner.Api.Models.Requests;

public class CreateTaskJournalCommentRequest
{
    public string Text { get; set; } = string.Empty;
}

public class CreateTaskJournalDeadlineChangeRequest
{
    public DateTime RequestedDueAt { get; set; }
    public string? Reason { get; set; }
}

public class ApproveTaskJournalDeadlineChangeRequest
{
    public DateTime ApprovedDueAt { get; set; }
    public string? Comment { get; set; }
}
