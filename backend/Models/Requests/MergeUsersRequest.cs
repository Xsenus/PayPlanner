namespace PayPlanner.Api.Models.Requests;

public class MergeUsersRequest
{
    public int TargetUserId { get; set; }
    public List<int> SourceUserIds { get; set; } = new();
    public string? PrimaryEmail { get; set; }
    public string? PrimaryPhoneNumber { get; set; }
}
