using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models;

/// <summary>
/// Категории справочника услуг.
/// </summary>
public class ServiceCategoryDictionaryItem
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#0EA5E9";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public ICollection<ServiceDictionaryItem> Services { get; set; } = new List<ServiceDictionaryItem>();
}
