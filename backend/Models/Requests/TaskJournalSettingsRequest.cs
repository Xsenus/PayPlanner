namespace PayPlanner.Api.Models.Requests;

public class TaskJournalSettingsRequest
{
    public bool RestrictExecutorEditing { get; set; } = true;
    public bool AllowExecutorDeadlineChangeRequests { get; set; } = true;
}
