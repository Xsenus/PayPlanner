namespace PayPlanner.Api.Models;

public class ContractInvoice
{
    public int ContractId { get; set; }
    public Contract Contract { get; set; } = null!;

    public int InvoiceId { get; set; }
    public Payment Invoice { get; set; } = null!;
}
