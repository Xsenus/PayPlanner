namespace PayPlanner.Api.Models.Requests;

public class UpsertTaskJournalItemRequest
{
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public bool IsInternal { get; set; }
    public int? ClientCaseId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? ApprovalDueAt { get; set; }
    public int? PlannedDurationMinutes { get; set; }
    public bool ContractorPaymentEnabled { get; set; }
    public decimal? ContractorPaymentAmount { get; set; }
    public DateTime? ContractorPaymentDueDate { get; set; }
    public bool ContractorPaymentOnResult { get; set; }
    public int? ContractorPaymentResultDelayDays { get; set; }
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "new";
    public string? Result { get; set; }
    public string? ChangeComment { get; set; }
    public List<string> Reminders { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<TaskJournalParticipantRequest> Participants { get; set; } = [];
}

public class TaskJournalParticipantRequest
{
    public int? UserId { get; set; }
    public string? Name { get; set; }
    public string Role { get; set; } = "assignee";
    public int? SortOrder { get; set; }
    public string? ApprovalStatus { get; set; }
    public string? Resolution { get; set; }
}

public class BulkUpdateTaskJournalItemsRequest
{
    public List<int> Ids { get; set; } = [];
    public int? AssigneeUserId { get; set; }
    public string? Priority { get; set; }
    public string? Status { get; set; }
    public DateTime? DueAt { get; set; }
    public string? Result { get; set; }
}

public class BulkDeleteTaskJournalItemsRequest
{
    public List<int> Ids { get; set; } = [];
}
