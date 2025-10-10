using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// DTO для создания/обновления компании (юридического лица).
/// </summary>
public class CompanyRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Идентификаторы клиентов, связанных с компанией.
    /// </summary>
    public ICollection<int> ClientIds { get; set; } = new List<int>();
}
