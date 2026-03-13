namespace PayPlanner.Api.Models.Requests;

public class InvoiceNumberingSettingsRequest
{
    public int StartNumber { get; set; } = 1;

    public string? AllocationStrategy { get; set; }
}
