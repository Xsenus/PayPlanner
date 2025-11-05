using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// DTO для создания/обновления компании (юридического лица).
/// </summary>
public class CompanyRequest
{
    /// <summary>
    /// Полное наименование компании.
    /// </summary>
    [MaxLength(400)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Краткое наименование компании.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string ShortName { get; set; } = string.Empty;

    /// <summary>
    /// Наименование для обратной совместимости (если shortName не передан).
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(12)]
    public string Inn { get; set; } = string.Empty;

    [MaxLength(9)]
    public string Kpp { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    [MaxLength(500)]
    public string ActualAddress { get; set; } = string.Empty;

    [MaxLength(500)]
    public string LegalAddress { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Идентификаторы клиентов, связанных с компанией.
    /// </summary>
    public ICollection<int> ClientIds { get; set; } = new List<int>();
}
