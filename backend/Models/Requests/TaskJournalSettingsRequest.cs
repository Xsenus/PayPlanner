using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public class TaskJournalSettingsRequest
{
    public bool RestrictExecutorEditing { get; set; } = true;
    public bool AllowExecutorDeadlineChangeRequests { get; set; } = true;
    public List<int> ExecutorCompletionAllowedUserIds { get; set; } = [];
    public bool RequireCompletionResult { get; set; } = true;
    public bool DailyOverdueReminderEnabled { get; set; } = true;
    public bool BirthdayTaskAutomationEnabled { get; set; }
    public bool EmployeeBirthdayTaskAutomationEnabled { get; set; }
    public int BirthdayTaskLeadDays { get; set; } = 7;
    public bool BirthdayClientUseSelectedAssignee { get; set; }
    public int? BirthdayTaskAssigneeUserId { get; set; }
    public int? CaseFinalReportAssigneeUserId { get; set; }
    public bool AllowAdminCompleteAnyTask { get; set; } = true;
    public int? FinancialDocumentsResponsibleUserId { get; set; }
    public int ApprovalDueDays { get; set; } = 5;
    public string? TaskCardWindowMode { get; set; } = "legacy";
    public string? ApprovalQuickActionMode { get; set; } = "completionButton";
    public bool CaseInactivityAutomationEnabled { get; set; } = true;
    public int CaseInactivityDays { get; set; } = 14;
    public int CaseInactivityLimit { get; set; } = 25;
    public int? CaseInactivityDefaultAssigneeUserId { get; set; }
    public int CaseInactivityEscalationDays { get; set; } = 30;
    public int? CaseInactivityEscalationUserId { get; set; }
    public string CaseInactivityRunLocalTime { get; set; } = "09:00";
    public string CaseInactivityScheduleTimeZone { get; set; } = "Europe/Moscow";
    public Dictionary<string, TaskStickerStyle>? StickerStyles { get; set; }
}
