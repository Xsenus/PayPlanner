namespace PayPlanner.Api.Models.Requests;

public class UpdateUserRequest
{
    public string? Email { get; set; }
    public string? FullName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public DateTime? DateOfBirth { get; set; }
    public string? PhotoUrl { get; set; }
    public string? PhoneNumber { get; set; }
    public string? WhatsApp { get; set; }
    public string? Telegram { get; set; }
    public string? Instagram { get; set; }
    public string? Messenger { get; set; }
    public string? Viber { get; set; }
    public bool? IsEmployee { get; set; }
    public string? Department { get; set; }
    public bool? RequiresFinancialActionReason { get; set; }
    public DateTime? EmploymentStartDate { get; set; }
    public DateTime? EmploymentEndDate { get; set; }
    public int? RoleId { get; set; }
    public bool? IsActive { get; set; }
    public List<int>? CompanyIds { get; set; }
}
