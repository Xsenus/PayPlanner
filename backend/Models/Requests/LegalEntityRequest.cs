using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public class LegalEntityRequest
{
    [Required]
    [MaxLength(200)]
    public string ShortName { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? FullName { get; set; }

    [MaxLength(20)]
    public string? Inn { get; set; }

    [MaxLength(20)]
    public string? Kpp { get; set; }

    [MaxLength(20)]
    public string? Ogrn { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    [MaxLength(100)]
    public string? Phone { get; set; }

    [EmailAddress]
    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(200)]
    public string? Director { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public List<int> ClientIds { get; set; } = new();
}
