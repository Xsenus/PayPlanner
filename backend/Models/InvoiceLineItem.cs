using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public class InvoiceLineItem
{
    public int Id { get; set; }

    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int SortOrder { get; set; }

    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    [MaxLength(32)]
    public string Unit { get; set; } = "шт";

    public decimal Price { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
