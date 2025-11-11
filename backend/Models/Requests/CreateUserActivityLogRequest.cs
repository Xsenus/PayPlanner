using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public class CreateUserActivityLogRequest
{
    [Required]
    [MaxLength(128)]
    public string Category { get; set; } = string.Empty;

    [Required]
    [MaxLength(160)]
    public string Action { get; set; } = string.Empty;

    [MaxLength(160)]
    public string? Section { get; set; }

    [MaxLength(160)]
    public string? ObjectType { get; set; }

    [MaxLength(160)]
    public string? ObjectId { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    public UserActivityStatus? Status { get; set; }

    public Dictionary<string, object?>? Metadata { get; set; }
}
