using System;
using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class TerminateContractRequest
{
    [Required]
    public DateTime TerminationDate { get; set; }

    [StringLength(1000)]
    public string? Reason { get; set; }

    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Terms { get; set; } = string.Empty;

    [StringLength(2000)]
    public string? FinancialTerms { get; set; }
}
