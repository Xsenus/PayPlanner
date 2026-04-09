namespace PayPlanner.Api.Models.Responses;

public class DeletedObjectAuditItemDto
{
    public string ObjectType { get; set; } = string.Empty;
    public int ObjectId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? UpdatedByUserName { get; set; }
    public DateTime? DeletedAt { get; set; }
    public string? DeletedByUserName { get; set; }
}
