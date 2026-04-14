namespace PayPlanner.Api.Models
{
    /// <summary>
    /// Справочник услуг для привязки к счетам.
    /// </summary>
    public class ServiceDictionaryItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Unit { get; set; } = "шт";
        public decimal DefaultPrice { get; set; } = 0m;
        public string ColorHex { get; set; } = "#0EA5E9";
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public int? CategoryId { get; set; }
        public ServiceCategoryDictionaryItem? Category { get; set; }

        public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    }
}
