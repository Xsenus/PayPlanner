namespace PayPlanner.Api.Models;

public class InvoiceResponsible
{
    public int InvoiceId { get; set; }
    public Payment Invoice { get; set; } = null!;

    public int UserId { get; set; }
    public User User { get; set; } = null!;
}

