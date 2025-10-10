using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

/// <summary>
/// Связь между физическим лицом (клиентом) и юридическим лицом (компанией).
/// </summary>
public class CompanyClient
{
    /// <summary>
    /// Идентификатор компании.
    /// </summary>
    public int CompanyId { get; set; }

    /// <summary>
    /// Компания, к которой относится сотрудник.
    /// </summary>
    public Company? Company { get; set; }

    /// <summary>
    /// Идентификатор клиента (физического лица).
    /// </summary>
    public int ClientId { get; set; }

    /// <summary>
    /// Клиент (физическое лицо).
    /// </summary>
    public Client? Client { get; set; }

    /// <summary>
    /// Дата создания связи.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Должность/роль клиента в компании.
    /// </summary>
    [MaxLength(200)]
    public string Role { get; set; } = string.Empty;
}
