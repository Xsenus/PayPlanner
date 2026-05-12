namespace PayPlanner.Api.Models;

public class DocumentStatusEntity
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#64748B";
    public bool IsActive { get; set; } = true;
    public bool UseForInvoices { get; set; } = true;
    public bool UseForActs { get; set; } = true;
    public bool UseForContracts { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
