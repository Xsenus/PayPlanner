using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class InvoiceLineItem
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int? ServiceId { get; set; }
    public ServiceDictionaryItem? Service { get; set; }

    public int SortOrder { get; set; }

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
