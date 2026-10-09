using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class ActLineItem
{
    public int Id { get; set; }

    public int ActId { get; set; }
    public Act Act { get; set; } = null!;

    public int SortOrder { get; set; }

    public int? InvoiceLineItemId { get; set; }

    [MaxLength(128)]
    public string SourceKey { get; set; } = string.Empty;

    public int? SourceSortOrder { get; set; }

    [MaxLength(2000)]
    public string Name { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    [MaxLength(32)]
    public string Unit { get; set; } = "шт";

    public decimal Price { get; set; }

    /// <summary>Код ставки НДС «в том числе» (см. <c>VatRates</c>); пусто — ставка не задана.</summary>
    [MaxLength(8)]
    public string? VatRate { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
