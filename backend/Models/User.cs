using System.Collections.Generic;

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
    public bool IsExternalContractor { get; set; } = false;
    public int? DepartmentId { get; set; }
    public string? Department { get; set; }
    public bool RequiresFinancialActionReason { get; set; } = false;
    public DateTime? EmploymentStartDate { get; set; }
    public DateTime? EmploymentEndDate { get; set; }

    public int RoleId { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsApproved { get; set; } = false;
    public DateTime? ApprovedAt { get; set; }
    public int? ApprovedByUserId { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? LastLogoutAt { get; set; }
    /// <summary>
    /// Версия серверной сессии. Увеличение значения немедленно отзывает все JWT,
    /// выпущенные пользователю с предыдущей версией.
    /// </summary>
    public int AuthTokenVersion { get; set; }
    public string? LastActivityAction { get; set; }
    public string? LastActivitySection { get; set; }
    public string? LastActivityIpAddress { get; set; }
    public string? LastActivityUserAgent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Role? Role { get; set; }
    public DepartmentDictionaryItem? DepartmentDictionary { get; set; }
    public User? ApprovedBy { get; set; }

    public ICollection<Act> Acts { get; set; } = new List<Act>();
    public ICollection<PaymentResponsible> InvoiceResponsibles { get; set; } = new List<PaymentResponsible>();
    public ICollection<UserExternalIdentity> ExternalIdentities { get; set; } = new List<UserExternalIdentity>();
    public ICollection<UserCompanyAccess> CompanyAccesses { get; set; } = new List<UserCompanyAccess>();
    public ICollection<RoleCalendarVisibleUser> CalendarVisibleInRoles { get; set; } = new List<RoleCalendarVisibleUser>();
}
