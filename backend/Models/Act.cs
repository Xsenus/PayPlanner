namespace PayPlanner.Api.Models;

public class Act
{
    public int Id { get; set; }
    public string Number { get; set; } = string.Empty;
    public string? Title { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public string? InvoiceNumber { get; set; }
    public string? CounterpartyInn { get; set; }
    public ActStatus Status { get; set; } = ActStatus.Created;
    public int? ClientId { get; set; }
    public Client? Client { get; set; }
    public int? ResponsibleId { get; set; }
    public User? Responsible { get; set; }
    public string? Comment { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
