namespace PayPlanner.Api.Models;

public class ContractPayment
{
    public int ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;
}
