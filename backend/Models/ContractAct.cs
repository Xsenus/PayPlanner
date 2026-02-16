namespace PayPlanner.Api.Models;

public class ContractAct
{
    public int ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    public int ActId { get; set; }
    public Act Act { get; set; } = null!;
}
