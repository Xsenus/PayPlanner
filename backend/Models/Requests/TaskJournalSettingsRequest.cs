namespace PayPlanner.Api.Models.Requests;

public class TaskJournalSettingsRequest
{
    public bool RestrictExecutorEditing { get; set; } = true;
    public bool AllowExecutorDeadlineChangeRequests { get; set; } = true;
    public bool RequireCompletionResult { get; set; } = true;
    public bool AllowAdminCompleteAnyTask { get; set; } = true;
    public int ApprovalDueDays { get; set; } = 5;
    public string? TaskCardWindowMode { get; set; } = "legacy";
    public bool CaseInactivityAutomationEnabled { get; set; } = true;
    public int CaseInactivityDays { get; set; } = 14;
    public int CaseInactivityLimit { get; set; } = 25;
    public int? CaseInactivityDefaultAssigneeUserId { get; set; }
    public int CaseInactivityEscalationDays { get; set; } = 30;
    public int? CaseInactivityEscalationUserId { get; set; }
}
