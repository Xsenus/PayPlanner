using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

/// <summary>
/// Запрос на создание или обновление договора.
/// </summary>
public class UpsertContractRequest
{
    [Required]
    [StringLength(100)]
    public string Number { get; set; } = string.Empty;

    [Required]
    public DateTime Date { get; set; }

    [StringLength(200)]
    public string? Title { get; set; }

    [StringLength(2000)]
    public string? Description { get; set; }

    [Required]
    [StringLength(32)]
    public string BillingMode { get; set; } = ContractBillingMode.Fixed;

    public bool IsOpenEnded { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335")]
    public decimal? Amount { get; set; }

    public DateTime? StartDate { get; set; }

    public DateTime? ValidUntil { get; set; }

    public DateTime? TerminationDate { get; set; }

    [MinLength(1, ErrorMessage = "Необходимо выбрать хотя бы одного клиента.")]
    public List<UpsertContractClientRequest> Clients { get; set; } = new();

    public List<int> ActIds { get; set; } = new();

    public List<int> InvoiceIds { get; set; } = new();

    public List<int> PaymentIds { get; set; } = new();
}

public class UpsertContractClientRequest
{
    public int ClientId { get; set; }

    [StringLength(1000)]
    public string? Comment { get; set; }
}
