namespace PayPlanner.Api.Models.Responses;

public class ExternalAuthStatusResponseDto
{
    public string Status { get; set; } = "pending";
    public bool Completed { get; set; }
    public string? Provider { get; set; }
    public string? Detail { get; set; }
    public string? ReturnUrl { get; set; }
    public bool RequiresApproval { get; set; }
    public bool IsInactive { get; set; }
    public bool Linked { get; set; }
    public string? Token { get; set; }
    public UserDto? User { get; set; }
}
