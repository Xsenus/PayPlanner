namespace PayPlanner.Api.Models;

public class LegalEntityContract
{
    public int LegalEntityId { get; set; }

    public LegalEntity LegalEntity { get; set; } = null!;

    public int ContractId { get; set; }

    public Contract Contract { get; set; } = null!;

    public string? Comment { get; set; }
}
