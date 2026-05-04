namespace PayPlanner.Api.Models.Requests;

public class UpsertTaskJournalItemRequest
{
    public string ObjectName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? AssigneeUserId { get; set; }
    public string? AssigneeName { get; set; }
    public DateTime DueAt { get; set; }
    public string Type { get; set; } = "task";
    public string Priority { get; set; } = "normal";
    public string Status { get; set; } = "new";
    public string? Result { get; set; }
    public List<string> Reminders { get; set; } = [];
    public List<string> Channels { get; set; } = [];
    public List<string> Tags { get; set; } = [];
}
