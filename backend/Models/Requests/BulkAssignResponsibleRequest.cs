namespace PayPlanner.Api.Models.Requests;

public class BulkAssignResponsibleRequest
{
    public List<int> Ids { get; set; } = new();
    public int ResponsibleUserId { get; set; }
}
