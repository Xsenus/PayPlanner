namespace PayPlanner.Api.Models;

public class PaymentResponsible
{
    public int PaymentId { get; set; }
    public Payment Payment { get; set; } = null!;

    public int UserId { get; set; }
    public User User { get; set; } = null!;
}
