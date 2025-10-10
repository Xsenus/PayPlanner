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
    /// Наименование компании.
    /// </summary>
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

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
    /// Юридический или почтовый адрес компании.
    /// </summary>
    [MaxLength(500)]
    public string Address { get; set; } = string.Empty;

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
