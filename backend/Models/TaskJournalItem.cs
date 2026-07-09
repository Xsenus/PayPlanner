using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalItem
{
    public int Id { get; set; }

    public int? ClientId { get; set; }
    public Client? Client { get; set; }

    public int? LegalEntityId { get; set; }
    public LegalEntity? LegalEntity { get; set; }

    public int? ClientCaseId { get; set; }
    public ClientCase? ClientCase { get; set; }

    [Required]
    [MaxLength(300)]
    public string ObjectName { get; set; } = string.Empty;

    [Required]
    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; set; }

    public int? AssigneeUserId { get; set; }
    public User? AssigneeUser { get; set; }

    [MaxLength(256)]
    public string AssigneeName { get; set; } = string.Empty;

    public int? AuthorUserId { get; set; }
    public User? AuthorUser { get; set; }

    [MaxLength(256)]
    public string AuthorName { get; set; } = string.Empty;

    public DateTime DueAt { get; set; }

    public DateTime? ApprovalDueAt { get; set; }

    public int PlannedDurationMinutes { get; set; } = 60;

    public bool ContractorPaymentEnabled { get; set; }

    public decimal? ContractorPaymentAmount { get; set; }

    public DateTime? ContractorPaymentDueDate { get; set; }

    public bool ContractorPaymentOnResult { get; set; }

    public int? ContractorPaymentResultDelayDays { get; set; }

    public int? ContractorPaymentId { get; set; }
    public Payment? ContractorPayment { get; set; }

    [MaxLength(32)]
    public string Type { get; set; } = "task";

    [MaxLength(32)]
    public string Priority { get; set; } = "normal";

    [MaxLength(32)]
    public string Status { get; set; } = "new";

    public bool IsUnderControl { get; set; }

    public DateTime? ControlMarkedAt { get; set; }

    public int? ControlMarkedByUserId { get; set; }
    public User? ControlMarkedByUser { get; set; }

    [MaxLength(256)]
    public string ControlMarkedByUserName { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Result { get; set; }

    public string RemindersJson { get; set; } = "[]";
    public string ChannelsJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";

    public ICollection<TaskJournalParticipant> Participants { get; set; } = new List<TaskJournalParticipant>();
    public ICollection<TaskJournalComment> Comments { get; set; } = new List<TaskJournalComment>();
    public ICollection<TaskJournalFile> Files { get; set; } = new List<TaskJournalFile>();
    public ICollection<TaskJournalDeadlineChangeRequest> DeadlineChangeRequests { get; set; } = new List<TaskJournalDeadlineChangeRequest>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
