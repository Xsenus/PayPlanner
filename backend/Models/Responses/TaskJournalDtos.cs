namespace PayPlanner.Api.Models.Responses;

public class TaskJournalItemDto
{
    public int Id { get; set; }
    public int? ClientId { get; set; }
    public string ClientName { get; set; } = string.Empty;
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
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "new";
    public string Result { get; set; } = string.Empty;
    public IReadOnlyList<string> Reminders { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Channels { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();
    public string CreatedAt { get; set; } = string.Empty;
    public string? UpdatedAt { get; set; }
}

public class TaskJournalAssigneeDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}
