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
    /// Дата договора (подписания).
    /// </summary>
    public DateTime Date { get; set; }

    /// <summary>
    /// Дата начала действия договора.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// Расширенное описание условий договора.
    /// </summary>
    public string? Description { get; set; }

    public string BillingMode { get; set; } = ContractBillingMode.Fixed;

    public string Status { get; set; } = ContractStatus.Draft;

    public DateTime? SentForSigningAt { get; set; }

    public DateTime? ConcludedAt { get; set; }

    public bool IsOpenEnded { get; set; }

    /// <summary>
    /// Сумма договора (если применимо).
    /// </summary>
    public decimal? Amount { get; set; }

    /// <summary>
    /// Дата окончания действия договора (если задана).
    /// </summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// Дата расторжения договора.
    /// </summary>
    public DateTime? TerminationDate { get; set; }

    public string? TerminationReason { get; set; }

    public string? TerminationTerms { get; set; }

    public string? TerminationFinancialTerms { get; set; }

    public DateTime? CancelledAt { get; set; }

    public string? CancelledReason { get; set; }

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

    /// <summary>
    /// Ручные привязки актов к договору.
    /// </summary>
    public ICollection<ContractAct> ContractActs { get; set; } = new List<ContractAct>();

    /// <summary>
    /// Ручные привязки счетов к договору.
    /// </summary>
    public ICollection<ContractInvoice> ContractInvoices { get; set; } = new List<ContractInvoice>();

    /// <summary>
    /// Ручные привязки начислений к договору.
    /// </summary>
    public ICollection<ContractPayment> ContractPayments { get; set; } = new List<ContractPayment>();

    /// <summary>
    /// Канонически связанные с договором документы платежного реестра.
    /// </summary>
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
