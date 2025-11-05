using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

/// <summary>
/// Представление юридического лица (компании).
/// </summary>
public class Company
{
    /// <summary>
    /// Уникальный идентификатор компании.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Наименование компании (короткое, для обратной совместимости).
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Полное наименование компании.
    /// </summary>
    [MaxLength(400)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>
    /// Краткое наименование компании.
    /// </summary>
    [MaxLength(200)]
    public string ShortName { get; set; } = string.Empty;

    /// <summary>
    /// Идентификационный номер налогоплательщика.
    /// </summary>
    [MaxLength(12)]
    public string Inn { get; set; } = string.Empty;

    /// <summary>
    /// Код причины постановки на учёт.
    /// </summary>
    [MaxLength(9)]
    public string Kpp { get; set; } = string.Empty;

    /// <summary>
    /// Контактный email.
    /// </summary>
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// Контактный телефон.
    /// </summary>
    [MaxLength(50)]
    public string Phone { get; set; } = string.Empty;

    /// <summary>
    /// Фактический адрес компании.
    /// </summary>
    [MaxLength(500)]
    public string ActualAddress { get; set; } = string.Empty;

    /// <summary>
    /// Юридический адрес компании.
    /// </summary>
    [MaxLength(500)]
    public string LegalAddress { get; set; } = string.Empty;

    /// <summary>
    /// Примечания по компании.
    /// </summary>
    [MaxLength(1000)]
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Флаг активности компании.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Дата создания записи (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Связанные сотрудники/контакты компании.
    /// </summary>
    public ICollection<CompanyClient> Members { get; set; } = new List<CompanyClient>();
}
