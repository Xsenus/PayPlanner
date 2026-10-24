namespace PayPlanner.Api.Models.Requests;

public class UpsertTaskJournalItemRequest
{
    public int? ClientId { get; set; }
    public int? LegalEntityId { get; set; }
    public bool IsInternal { get; set; }
    public bool IsUntriaged { get; set; }
    public int? ClientCaseId { get; set; }
    public string? ClientBillingClassification { get; set; }
    public int? BillingContractId { get; set; }
    public string ObjectName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public int? AuthorUserId { get; set; }
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
    public bool IsUnderControl { get; set; }
    public string? Result { get; set; }
    public string? ChangeComment { get; set; }
    public List<string> Reminders { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<TaskJournalParticipantRequest> Participants { get; set; } = [];
    public bool RecurrenceEnabled { get; set; }
    public string? RecurrenceFrequency { get; set; }
    public int? RecurrenceInterval { get; set; }
    public int? RecurrenceDayOfWeek { get; set; }
    public int? RecurrenceDayOfMonth { get; set; }
    public int? RecurrenceMonth { get; set; }
    public int? RecurrenceTimeMinutes { get; set; }
    public bool RecurrenceSkipWeekends { get; set; }
    public string? RecurrenceTimeZoneId { get; set; }
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
    public string? BillingClassification { get; set; }
    public bool IncludeCompleted { get; set; }
    public Dictionary<int, string?>? ExpectedUpdatedAtById { get; set; }
}

public class BulkDeleteTaskJournalItemsRequest
{
    public List<int> Ids { get; set; } = [];
}

public class UpdateTaskJournalControlRequest
{
    public bool IsUnderControl { get; set; }
}

public sealed class UpdateTaskBillingClassificationRequest
{
    public string? Classification { get; set; }
    public int? BillingContractId { get; set; }
}

public sealed class LinkTaskBillingInvoiceRequest
{
    public int InvoiceId { get; set; }
    public bool ConfirmedIncludedInInvoice { get; set; }
}
