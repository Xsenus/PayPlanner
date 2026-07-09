using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class TaskJournalTemplate
{
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; set; }

    [MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? TaskDescription { get; set; }

    [MaxLength(32)]
    public string Type { get; set; } = "task";

    [MaxLength(32)]
    public string Priority { get; set; } = "normal";

    public int PlannedDurationMinutes { get; set; } = 60;

    public int DueOffsetMinutes { get; set; } = 1440;

    public int? AssigneeUserId { get; set; }
    public User? AssigneeUser { get; set; }

    [MaxLength(256)]
    public string AssigneeName { get; set; } = string.Empty;

    [MaxLength(32)]
    public string BindingType { get; set; } = "none";

    [MaxLength(120)]
    public string BindingKey { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public string RemindersJson { get; set; } = "[]";
    public string ChannelsJson { get; set; } = "[]";
    public string TagsJson { get; set; } = "[]";

    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    [MaxLength(256)]
    public string CreatedByName { get; set; } = string.Empty;

    public ICollection<TaskJournalTemplateParticipant> Participants { get; set; } = new List<TaskJournalTemplateParticipant>();

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
