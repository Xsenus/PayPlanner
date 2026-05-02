using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class CancelInvoiceRequest
{
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = string.Empty;
}
