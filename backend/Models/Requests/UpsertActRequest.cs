using System.ComponentModel.DataAnnotations;
using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Requests;

public class UpsertActRequest
{
    [Required]
    [MaxLength(100)]
    public string Number { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Title { get; set; }

    [Required]
    public DateTime Date { get; set; }

    [Range(0, double.MaxValue)]
    public decimal Amount { get; set; }

    [MaxLength(100)]
    public string? InvoiceNumber { get; set; }

    [MaxLength(32)]
    public string? CounterpartyInn { get; set; }

    [Required]
    public ActStatus Status { get; set; } = ActStatus.Created;

    public int? ClientId { get; set; }

    public int? ResponsibleId { get; set; }

    [MaxLength(1000)]
    public string? Comment { get; set; }
}
