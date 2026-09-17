using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public class TaskJournalItemDto
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int? LegalEntityId { get; set; }
    public string LegalEntityName { get; set; } = string.Empty;
    public bool IsInternal { get; set; }
    public int? ClientCaseId { get; set; }
    public string ClientCaseTitle { get; set; } = string.Empty;
    public string ClientBillingClassification { get; set; } = TaskClientBillingClassification.Legacy;
    public int? BillingContractId { get; set; }
    public string BillingContractNumber { get; set; } = string.Empty;
    public string BillingMode { get; set; } = string.Empty;
    public int? BillingInvoiceId { get; set; }
    public string BillingInvoiceNumber { get; set; } = string.Empty;
    public string BillingStatus { get; set; } = "legacy";
    public string ObjectName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int? AssigneeUserId { get; set; }
    public string AssigneeName { get; set; } = string.Empty;
    public int? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string DueAt { get; set; } = string.Empty;
    public string? ApprovalDueAt { get; set; }
    public int PlannedDurationMinutes { get; set; } = 60;
    public bool ContractorPaymentEnabled { get; set; }
    public decimal? ContractorPaymentAmount { get; set; }
    public string? ContractorPaymentDueDate { get; set; }
    public bool ContractorPaymentOnResult { get; set; }
    public int? ContractorPaymentResultDelayDays { get; set; }
    public int? ContractorPaymentId { get; set; }
    public string ContractorPaymentStatus { get; set; } = string.Empty;
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "new";
    public bool IsUnderControl { get; set; }
    public string? ControlMarkedAt { get; set; }
    public int? ControlMarkedByUserId { get; set; }
    public string ControlMarkedByUserName { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public IReadOnlyList<string> Reminders { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Channels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TaskJournalParticipantDto> Participants { get; set; } = Array.Empty<TaskJournalParticipantDto>();
    public IReadOnlyList<TaskJournalDeadlineChangeRequestDto> ActiveDeadlineChangeRequests { get; set; } = Array.Empty<TaskJournalDeadlineChangeRequestDto>();
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
}

public sealed class TaskBillingContextDto
{
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public int ClientCaseId { get; set; }
    public string ClientCaseTitle { get; set; } = string.Empty;
    public IReadOnlyList<TaskBillingContractOptionDto> Contracts { get; set; } = Array.Empty<TaskBillingContractOptionDto>();
}

public sealed class TaskBillingContractOptionDto
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BillingMode { get; set; } = ContractBillingMode.Fixed;
    public string Status { get; set; } = string.Empty;
    public decimal? Amount { get; set; }
    public string Date { get; set; } = string.Empty;
}

public class TaskJournalParticipantDto
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Role { get; set; } = "assignee";
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string ApprovalStatus { get; set; } = "pending";
    public string Resolution { get; set; } = string.Empty;
    public string? RespondedAt { get; set; }
}

public class TaskJournalTemplateDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string TaskDescription { get; set; } = string.Empty;
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public int PlannedDurationMinutes { get; set; } = 60;
    public int DueOffsetMinutes { get; set; } = 1440;
    public int? AssigneeUserId { get; set; }
    public string AssigneeName { get; set; } = string.Empty;
    public string BindingType { get; set; } = "none";
    public string BindingKey { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public IReadOnlyList<string> Reminders { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Channels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TaskJournalTemplateParticipantDto> Participants { get; set; } = Array.Empty<TaskJournalTemplateParticipantDto>();
    public int? CreatedByUserId { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
}

public class TaskJournalTemplateParticipantDto
{
    public int Id { get; set; }
    public int? UserId { get; set; }
    public string Role { get; set; } = "observer";
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public class TaskJournalCommentDto
{
    public int Id { get; set; }
    public int TaskJournalItemId { get; set; }
    public int? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Kind { get; set; } = "comment";
    public string Text { get; set; } = string.Empty;
    public bool IsExternalCalendarVisible { get; set; }
    public string? ExternalSource { get; set; }
    public string? ExternalAuthorTrust { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
}

public class TaskJournalDeadlineChangeRequestDto
{
    public int Id { get; set; }
    public int TaskJournalItemId { get; set; }
    public int? RequestedByUserId { get; set; }
    public string RequestedByName { get; set; } = string.Empty;
    public int? ReviewerUserId { get; set; }
    public string ReviewerName { get; set; } = string.Empty;
    public string CurrentDueAt { get; set; } = string.Empty;
    public string RequestedDueAt { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
    public string? DecisionDueAt { get; set; }
    public string DecisionComment { get; set; } = string.Empty;
    public int? DecidedByUserId { get; set; }
    public string DecidedByName { get; set; } = string.Empty;
    public string? DecidedAt { get; set; }
    public int? CreatedCommentId { get; set; }
    public int? DecisionCommentId { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
}

public class TaskJournalFileDto
{
    public int Id { get; set; }
    public int TaskJournalItemId { get; set; }
    public string DocumentKey { get; set; } = string.Empty;
    public int Version { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public int? UploadedByUserId { get; set; }
    public string UploadedByName { get; set; } = string.Empty;
    public string UploadedAt { get; set; } = string.Empty;
    public bool IsLatestVersion { get; set; }
    public int? ClientCaseDocumentId { get; set; }
}

public class TaskJournalAssigneeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsExternalContractor { get; set; }
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public string? TimeZoneId { get; set; }
}

public class TaskJournalCaseAutomationCandidateDto
{
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int? LegalEntityId { get; set; }
    public string LegalEntityName { get; set; } = string.Empty;
    public int? ResponsibleUserId { get; set; }
    public string ResponsibleName { get; set; } = string.Empty;
    public int ClientCaseId { get; set; }
    public string ClientCaseTitle { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int ActiveTaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
    public int DaysWithoutActiveTask { get; set; }
    public bool IsEscalated { get; set; }
    public bool IsReadyForControl { get; set; }
    public string AutomationState { get; set; } = "ready";
    public string LastActivityAt { get; set; } = string.Empty;
    public string LastActivitySource { get; set; } = string.Empty;
    public string? LastTaskAt { get; set; }
    public string? NextControlAt { get; set; }
    public int DaysUntilControl { get; set; }
    public string SuggestedTitle { get; set; } = string.Empty;
    public string SuggestedDescription { get; set; } = string.Empty;
    public string SuggestedDueAt { get; set; } = string.Empty;
    public string SuggestedPriority { get; set; } = "high";
    public int? SuggestedAssigneeUserId { get; set; }
    public string SuggestedAssigneeName { get; set; } = string.Empty;
    public string SuggestedAssigneeSource { get; set; } = "responsible";
    public string SuggestedAssigneeTimeZoneId { get; set; } = "Europe/Moscow";
    public string? ResponsibleVacationUntilDate { get; set; }
}

public class TaskJournalCaseAutomationSummaryDto
{
    public int TotalCount { get; set; }
    public int ReadyCount { get; set; }
    public int CriticalCount { get; set; }
    public int WaitingCount { get; set; }
    public int PausedCount { get; set; }
    public string? NextStateChangeAt { get; set; }
    public string? NextStateChangeKind { get; set; }
}

public class TaskJournalAutomationCreateResultDto
{
    public IReadOnlyList<TaskJournalItemDto> CreatedTasks { get; set; } = Array.Empty<TaskJournalItemDto>();
    public int SkippedCount { get; set; }
}

public class TaskJournalAutomationDiagnosticsDto
{
    public bool Enabled { get; set; }
    public int InactivityDays { get; set; }
    public int EscalationDays { get; set; }
    public int Limit { get; set; }
    public int? DefaultAssigneeUserId { get; set; }
    public string DefaultAssigneeName { get; set; } = string.Empty;
    public bool DefaultAssigneeAvailable { get; set; }
    public int? EscalationUserId { get; set; }
    public string EscalationUserName { get; set; } = string.Empty;
    public int VisibleCandidateCount { get; set; }
    public int NextRunCandidateCount { get; set; }
    public int DeferredByLimitCount { get; set; }
    public int VisibleUnassignedCount { get; set; }
    public int VisibleEscalationCandidateCount { get; set; }
    public int CreatedTaskCountLast7Days { get; set; }
    public bool ScheduleDueNow { get; set; }
    public string? NextScheduledRunAt { get; set; }
    public string? LastCreatedAt { get; set; }
    public string? LastCreatedDescription { get; set; }
}

public class TaskJournalAutomationPreviewDto
{
    public int InactivityDays { get; set; }
    public int EscalationDays { get; set; }
    public int DailyLimit { get; set; }
    public int TrackedCaseCount { get; set; }
    public int CandidateCount { get; set; }
    public int NextRunCandidateCount { get; set; }
    public int DeferredByLimitCount { get; set; }
    public int EscalationCandidateCount { get; set; }
    public int ExcludedByStatusCount { get; set; }
    public int PausedByVacationCount { get; set; }
    public int DisabledCount { get; set; }
    public int ActiveTaskCount { get; set; }
    public int UnassignedCount { get; set; }
    public int RecentlyActiveCount { get; set; }
}

public class TaskJournalAutomationPreviewDetailsDto
{
    public string Category { get; set; } = string.Empty;
    public int InactivityDays { get; set; }
    public int EscalationDays { get; set; }
    public int TotalCount { get; set; }
    public int Offset { get; set; }
    public int Limit { get; set; }
    public List<TaskJournalAutomationPreviewCaseDto> Items { get; set; } = [];
}

public class TaskJournalAutomationPreviewCaseDto
{
    public int ClientCaseId { get; set; }
    public string ClientCaseTitle { get; set; } = string.Empty;
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
    public int? LegalEntityId { get; set; }
    public string LegalEntityName { get; set; } = string.Empty;
    public string ResponsibleName { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string? LastActivityAt { get; set; }
    public string? LastActivitySource { get; set; }
    public string? FirstControlDueAt { get; set; }
    public string? CriticalControlDueAt { get; set; }
}

public class TaskJournalSettingsDto
{
    public bool RestrictExecutorEditing { get; set; } = true;
    public bool AllowExecutorDeadlineChangeRequests { get; set; } = true;
    public List<int> ExecutorCompletionAllowedUserIds { get; set; } = [];
    public bool RequireCompletionResult { get; set; } = true;
    public bool DailyOverdueReminderEnabled { get; set; } = true;
    public bool AllowAdminCompleteAnyTask { get; set; } = true;
    public int? FinancialDocumentsResponsibleUserId { get; set; }
    public string FinancialDocumentsResponsibleUserName { get; set; } = string.Empty;
    public int ApprovalDueDays { get; set; } = 5;
    public string TaskCardWindowMode { get; set; } = "legacy";
    public string ApprovalQuickActionMode { get; set; } = "completionButton";
    public bool CaseInactivityAutomationEnabled { get; set; } = true;
    public int CaseInactivityDays { get; set; } = 14;
    public int CaseInactivityLimit { get; set; } = 25;
    public int? CaseInactivityDefaultAssigneeUserId { get; set; }
    public int CaseInactivityEscalationDays { get; set; } = 30;
    public int? CaseInactivityEscalationUserId { get; set; }
    public int CaseInactivityFirstRepeatAfterDays { get; set; } = 2;
    public int CaseInactivityCriticalRepeatAfterDays { get; set; } = 4;
    public string CaseInactivityRunLocalTime { get; set; } = "09:00";
    public string CaseInactivityScheduleTimeZone { get; set; } = "Europe/Moscow";
    public Dictionary<string, TaskStickerStyle> StickerStyles { get; set; } = [];
}

public class TaskJournalOverdueSummaryDto
{
    public int Count { get; set; }
}

/// <summary>
/// Компактные счётчики журнала задач. Они рассчитываются в базе и позволяют
/// странице показывать метрики без выгрузки всех карточек задач в браузер.
/// </summary>
public class TaskJournalListSummaryDto
{
    public int Active { get; set; }
    public int Overdue { get; set; }
    public int Today { get; set; }
    public int HighPriority { get; set; }
    public int Unbilled { get; set; }
    public int InvoicesToPrepare { get; set; }
    public int Fixed { get; set; }
    public int Invoiced { get; set; }
    public int NonBillable { get; set; }
    public int Unclassified { get; set; }
}
