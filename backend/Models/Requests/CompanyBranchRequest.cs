using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class CompanyBranchRequest
{
    /// <summary>Branch, Office, Point или Direction.</summary>
    [MaxLength(16)]
    public string? Kind { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(32)]
    public string? Code { get; set; }

    /// <summary>Active или Archived; при создании по умолчанию Active.</summary>
    [MaxLength(16)]
    public string? Status { get; set; }

    [MaxLength(20)]
    public string? Kpp { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(64)]
    public string? BankAccountId { get; set; }

    [MaxLength(8000)]
    public string? SignerJson { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }
}
