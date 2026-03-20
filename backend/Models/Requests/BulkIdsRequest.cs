namespace PayPlanner.Api.Models.Requests;

public class BulkIdsRequest
{
    public List<int> Ids { get; set; } = new();
}
