namespace PayPlanner.Api.Models.Requests;

public class ExternalAuthStartRequest
{
    public string Provider { get; set; } = string.Empty;
    public string? ReturnUrl { get; set; }
}
