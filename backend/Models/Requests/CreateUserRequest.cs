namespace PayPlanner.Api.Models.Requests;

public class CreateUserRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsEmployee { get; set; } = false;
    public bool IsExternalContractor { get; set; } = false;
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public bool RequiresFinancialActionReason { get; set; } = false;
    public List<int>? CompanyIds { get; set; }
}
