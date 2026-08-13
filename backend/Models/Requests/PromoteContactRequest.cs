namespace PayPlanner.Api.Models.Requests;

public class PromoteContactRequest
{
    public string Target { get; set; } = string.Empty;
    public string Role { get; set; } = "Client";
}
