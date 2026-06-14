using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class CreateActCorrectionRequest
{
    [StringLength(1000)]
    public string? Reason { get; set; }
}
