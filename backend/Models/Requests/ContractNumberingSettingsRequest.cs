namespace PayPlanner.Api.Models.Requests;

public class ContractNumberingSettingsRequest
{
    public int StartNumber { get; set; } = 1;

    public string? AllocationStrategy { get; set; }

    public string? Template { get; set; }
}
