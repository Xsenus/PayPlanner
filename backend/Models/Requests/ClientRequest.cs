using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// DTO для создания/обновления клиента (физического лица).
/// </summary>
public class ClientRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Company { get; set; } = string.Empty;

    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Список идентификаторов компаний, связанных с клиентом.
    /// </summary>
    public ICollection<int> CompanyIds { get; set; } = new List<int>();
}
