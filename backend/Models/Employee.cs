using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Employee directory with lifecycle tracking
/// Represents staff members who can be assigned to cases and incur expenses
/// </summary>
[Table("employees")]
public class Employee
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("first_name")]
    [MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [Column("last_name")]
    [MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [Column("middle_name")]
    [MaxLength(100)]
    public string? MiddleName { get; set; }

    [Required]
    [Column("role")]
    [MaxLength(100)]
    public string Role { get; set; } = string.Empty; // lawyer, paralegal, admin, etc.

    [Required]
    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "active"; // active, terminated

    [Column("hire_date")]
    public DateTime HireDate { get; set; }

    [Column("termination_date")]
    public DateTime? TerminationDate { get; set; }

    [Column("phone")]
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [Column("email")]
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [Column("address")]
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [Column("notes")]
    [MaxLength(2000)]
    public string Notes { get; set; } = string.Empty;

    [Column("user_id")]
    public string? UserId { get; set; } // Link to auth user if applicable

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<CaseEmployee> CaseAssignments { get; set; } = new List<CaseEmployee>();
    public virtual ICollection<Expense> Expenses { get; set; } = new List<Expense>();
}

[Table("case_employees")]
public class CaseEmployee
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("case_id")]
    public int CaseId { get; set; }

    [Required]
    [Column("employee_id")]
    public int EmployeeId { get; set; }

    [Required]
    [Column("role_in_case")]
    [MaxLength(50)]
    public string RoleInCase { get; set; } = string.Empty; // lead, assistant, consultant

    [Column("assigned_at")]
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("CaseId")]
    public virtual ClientCase Case { get; set; } = null!;

    [ForeignKey("EmployeeId")]
    public virtual Employee Employee { get; set; } = null!;
}

public static class EmployeeRoles
{
    public const string Lawyer = "lawyer";
    public const string Paralegal = "paralegal";
    public const string Admin = "admin";
    public const string Accountant = "accountant";
}

public static class CaseRoles
{
    public const string Lead = "lead";
    public const string Assistant = "assistant";
    public const string Consultant = "consultant";
}
