namespace PayPlanner.Api.Models;

public class InvoiceItemNameDictionaryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Unit { get; set; } = "шт";
    public decimal DefaultPrice { get; set; } = 0m;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#334155";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
