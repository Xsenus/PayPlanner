namespace PayPlanner.Api.Models;

public class MoneyAccount
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MoneyOperationSource Source { get; set; }
    public string Currency { get; set; } = "RUB";
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<MoneyOperation> MoneyOperations { get; set; } = new List<MoneyOperation>();
}
