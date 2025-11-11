using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models;

/// <summary>
/// Модель договора между компанией и клиентами.
/// </summary>
public class Contract
{
    /// <summary>
    /// Уникальный идентификатор договора.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Номер договора.
    /// </summary>
    public string Number { get; set; } = string.Empty;

    /// <summary>
    /// Заголовок или короткое описание договора.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Дата подписания договора.
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Расширенное описание условий договора.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Сумма договора (если применимо).
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// Дата окончания действия договора (если задана).
    /// </summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// Дата и время создания записи.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Дата и время последнего обновления записи.
    /// </summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>
    /// Навигационное свойство для связи многие-ко-многим с клиентами.
    /// </summary>
    public ICollection<ClientContract> ClientContracts { get; set; } = new List<ClientContract>();

    /// <summary>
    /// Клиенты, привязанные к договору.
    /// </summary>
    public ICollection<Client> Clients { get; set; } = new List<Client>();
}
