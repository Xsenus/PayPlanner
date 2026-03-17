namespace PayPlanner.Api.Models;

public class InvoiceClientCase
{
    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;
}
