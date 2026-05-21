using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ClientCaseCourt
{
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Instance { get; set; } = "first";

    [MaxLength(100)]
    public string Code { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Url { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Contacts { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = string.Empty;

    public DateTime? UpdatedAt { get; set; }

    public int? UpdatedByUserId { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = string.Empty;
}
