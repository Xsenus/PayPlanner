namespace PayPlanner.Api.Models.Requests;

public class UpsertTaskJournalTemplateRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? TaskDescription { get; set; }
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public int? PlannedDurationMinutes { get; set; }
    public int? DueOffsetMinutes { get; set; }
    public int? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public string BindingType { get; set; } = "none";
    public string? BindingKey { get; set; }
    public bool IsActive { get; set; } = true;
    public List<string> Reminders { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<TaskJournalTemplateParticipantRequest> Participants { get; set; } = [];
}

public class TaskJournalTemplateParticipantRequest
{
    public int? UserId { get; set; }
    public string? Name { get; set; }
    public string Role { get; set; } = "observer";
    public int? SortOrder { get; set; }
}

public class UpdateTaskJournalTemplateActiveRequest
{
    public bool IsActive { get; set; }
}
