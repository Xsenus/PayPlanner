using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// Запрос на создание или обновление договора.
/// </summary>
public class UpsertContractRequest
{
    /// <summary>
    /// Номер договора.
    /// </summary>
    [Required]
    [StringLength(100)]
    public string Number { get; set; } = string.Empty;

    /// <summary>
    /// Дата договора.
    /// </summary>
    [Required]
    public DateTime Date { get; set; }

    /// <summary>
    /// Заголовок или краткое описание договора.
    /// </summary>
    [StringLength(200)]
    public string? Title { get; set; }

    /// <summary>
    /// Полное описание договора.
    /// </summary>
    [StringLength(2000)]
    public string? Description { get; set; }

    /// <summary>
    /// Сумма договора (если указана).
    /// </summary>
    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? Amount { get; set; }

    /// <summary>
    /// Дата окончания действия договора.
    /// </summary>
    public DateTime? ValidUntil { get; set; }

    /// <summary>
    /// Идентификаторы клиентов, связанных с договором.
    /// </summary>
    [MinLength(1, ErrorMessage = "Необходимо выбрать хотя бы одного клиента.")]
    public List<int> ClientIds { get; set; } = new();
}
