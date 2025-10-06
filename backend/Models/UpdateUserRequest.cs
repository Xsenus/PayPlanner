namespace PayPlanner.Api.Models;

public class UpdateUserRequest
{
    public string FullName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public bool IsActive { get; set; }
}
