namespace PayPlanner.Api.Models.Responses
{
    public class LoginResponse
    {
        public string Token { get; set; } = string.Empty;
        public UserDto User { get; set; } = null!;
    }

    public class UserDto
    {
        public int Id { get; set; }
        public string Email { get; set; } = string.Empty;
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
        public bool IsEmployee { get; set; }
        public string? Department { get; set; }
        public bool RequiresFinancialActionReason { get; set; }
        public DateTime? EmploymentStartDate { get; set; }
        public DateTime? EmploymentEndDate { get; set; }
        public RoleDto Role { get; set; } = null!;
        public bool IsActive { get; set; }
        public bool IsApproved { get; set; }
        public bool IsProtectedUser { get; set; }
        public DateTime? ApprovedAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string? CreatedByUserName { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string? UpdatedByUserName { get; set; }
        public List<int> CompanyIds { get; set; } = new();
        public List<UserCompanyAccessDto> Companies { get; set; } = new();
        public List<ExternalIdentityDto> ExternalIdentities { get; set; } = new();
    }

    public class UserCompanyAccessDto
    {
        public int Id { get; set; }
        public string ShortName { get; set; } = string.Empty;
        public string? FullName { get; set; }
        public string? Inn { get; set; }
        public string? Kpp { get; set; }
    }

    public class RoleDto
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime? CreatedAt { get; set; }
        public int? CreatedByUserId { get; set; }
        public string? CreatedByUserName { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public int? UpdatedByUserId { get; set; }
        public string? UpdatedByUserName { get; set; }
        public bool IsSystemRole { get; set; }
        public int AssignedUsersCount { get; set; }
        public int AccessibleSectionsCount { get; set; }
        public int TotalSectionsCount { get; set; }
        public DateTime? PermissionsUpdatedAt { get; set; }
        public int? PermissionsUpdatedByUserId { get; set; }
        public string? PermissionsUpdatedByUserName { get; set; }
    }
}
