namespace PayPlanner.Api.Models;

public class User
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;

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

    public bool IsEmployee { get; set; } = false;
    public DateTime? EmploymentStartDate { get; set; }
    public DateTime? EmploymentEndDate { get; set; }

    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsApproved { get; set; } = false;
    public DateTime? ApprovedAt { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Role? Role { get; set; }
    public User? ApprovedBy { get; set; }
}
