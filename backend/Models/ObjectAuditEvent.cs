namespace PayPlanner.Api.Models;

public class ObjectAuditEvent
{
    public int Id { get; set; }
    public string ObjectType { get; set; } = string.Empty;
    public int ObjectId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? ActorUserId { get; set; }
    public string? ActorUserName { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
}
