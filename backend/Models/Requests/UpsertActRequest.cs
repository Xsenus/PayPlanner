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

    [MaxLength(32)]
    public string CreationMode { get; set; } = ActCreationMode.Manual;

    [MaxLength(100)]
    public string? InvoiceNumber { get; set; }

    public int? InvoiceId { get; set; }

    [MaxLength(32)]
    public string? CounterpartyInn { get; set; }

    [Required]
    public ActStatus Status { get; set; } = ActStatus.Created;

    [MaxLength(32)]
    public string? WorkflowStatus { get; set; }

    public int? ClientId { get; set; }

    public int? LegalEntityId { get; set; }

    public int? ContractId { get; set; }

    public int? ResponsibleId { get; set; }

    public string? Comment { get; set; }

    public List<ActLineItemRequest> LineItems { get; set; } = new();
}

public class ActLineItemRequest
{
    [Required]
    [MaxLength(128)]
    public string SourceKey { get; set; } = string.Empty;

    [MaxLength(2000)]
    public string? Name { get; set; }

    [Range(typeof(decimal), "0.0000001", "79228162514264337593543950335", ParseLimitsInInvariantCulture = true)]
    public decimal Quantity { get; set; }

    [MaxLength(32)]
    public string? Unit { get; set; }

    [Range(typeof(decimal), "0", "79228162514264337593543950335", ParseLimitsInInvariantCulture = true)]
    public decimal Price { get; set; }
}
