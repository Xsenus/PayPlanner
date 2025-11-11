using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models;

/// <summary>
/// Справочник статусов клиента (например, клиент, поставщик, кредитор).
/// </summary>
public class ClientStatusEntity
{
    /// <summary>
    /// Цвет статуса в HEX-формате.
    /// </summary>
    public string ColorHex { get; set; } = "#2563EB";

    /// <summary>
    /// Дата создания записи (UTC).
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Дополнительное описание статуса.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор статуса.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Признак активного статуса.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Название статуса.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Клиенты, у которых установлен данный статус.
    /// </summary>
    public ICollection<Client> Clients { get; set; } = new List<Client>();
}
