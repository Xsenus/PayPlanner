using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

/// <summary>
/// Unified Expense entity that can reference employees and optionally cases
/// Single source of truth - visible in both employee and case views without duplication
/// </summary>
[Table("expenses")]
public class Expense
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [Column("expense_date")]
    public DateTime ExpenseDate { get; set; }

    [Required]
    [Column("amount")]
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    [Required]
    [Column("category")]
    [MaxLength(50)]
    public string Category { get; set; } = string.Empty; // Salary, CaseWork, Bonus, Reimbursement, Other

    [Required]
    [Column("purpose_text")]
    [MaxLength(500)]
    public string PurposeText { get; set; } = string.Empty;

    [Required]
    [Column("payee_type")]
    [MaxLength(50)]
    public string PayeeType { get; set; } = string.Empty; // employee, vendor, contractor

    [Required]
    [Column("payee_id")]
    public int PayeeId { get; set; } // References different tables based on payee_type

    [Column("employee_id")]
    public int? EmployeeId { get; set; } // Direct link for employee-related expenses

    [Column("case_id")]
    public int? CaseId { get; set; } // Required when category = CaseWork

    [Column("client_id")]
    public int? ClientId { get; set; } // Required when category = CaseWork

    [Column("company_id")]
    public int? CompanyId { get; set; } // If using new company model

    [Column("person_id")]
    public int? PersonId { get; set; } // If using new person model

    [Column("created_by")]
    public string CreatedBy { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("EmployeeId")]
    public virtual Employee? Employee { get; set; }

    [ForeignKey("CaseId")]
    public virtual ClientCase? Case { get; set; }

    [ForeignKey("ClientId")]
    public virtual Client? Client { get; set; }

    [ForeignKey("CompanyId")]
    public virtual Company? Company { get; set; }

    [ForeignKey("PersonId")]
    public virtual Person? Person { get; set; }
}

public static class ExpenseCategories
{
    public const string Salary = "Salary";
    public const string CaseWork = "CaseWork";
    public const string Bonus = "Bonus";
    public const string Reimbursement = "Reimbursement";
    public const string Other = "Other";
}

public static class PayeeTypes
{
    public const string Employee = "employee";
    public const string Vendor = "vendor";
    public const string Contractor = "contractor";
}
