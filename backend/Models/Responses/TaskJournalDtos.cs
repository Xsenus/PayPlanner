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
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
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

public class TaskJournalCommentDto
{
    public int Id { get; set; }
    public int TaskJournalItemId { get; set; }
    public int? AuthorUserId { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string Kind { get; set; } = "comment";
    public string Text { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
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
    public int ClientCaseId { get; set; }
    public string ClientCaseTitle { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public int ActiveTaskCount { get; set; }
    public int CompletedTaskCount { get; set; }
    public int DaysWithoutActiveTask { get; set; }
    public bool IsEscalated { get; set; }
    public string? LastTaskAt { get; set; }
    public string SuggestedTitle { get; set; } = string.Empty;
    public string SuggestedDescription { get; set; } = string.Empty;
    public string SuggestedDueAt { get; set; } = string.Empty;
    public string SuggestedPriority { get; set; } = "high";
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
    public int? EscalationUserId { get; set; }
    public string EscalationUserName { get; set; } = string.Empty;
    public int VisibleCandidateCount { get; set; }
    public int VisibleEscalationCandidateCount { get; set; }
    public int CreatedTaskCountLast7Days { get; set; }
    public string? LastCreatedAt { get; set; }
    public string? LastCreatedDescription { get; set; }
}

public class TaskJournalSettingsDto
{
    public bool RestrictExecutorEditing { get; set; } = true;
    public bool AllowExecutorDeadlineChangeRequests { get; set; } = true;
    public bool RequireCompletionResult { get; set; } = true;
    public string TaskCardWindowMode { get; set; } = "legacy";
    public bool CaseInactivityAutomationEnabled { get; set; } = true;
    public int CaseInactivityDays { get; set; } = 14;
    public int CaseInactivityLimit { get; set; } = 25;
    public int? CaseInactivityDefaultAssigneeUserId { get; set; }
    public int CaseInactivityEscalationDays { get; set; } = 30;
    public int? CaseInactivityEscalationUserId { get; set; }
}
