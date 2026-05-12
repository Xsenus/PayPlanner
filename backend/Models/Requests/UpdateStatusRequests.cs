using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class UpdateNamedStatusRequest
{
    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = string.Empty;
}

public class UpdatePaymentStatusRequest
{
    [Required]
    [MaxLength(32)]
    public string Status { get; set; } = string.Empty;

    public DateTime? PaidDate { get; set; }
}
