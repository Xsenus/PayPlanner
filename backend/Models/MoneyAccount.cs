using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations.Schema;

namespace PayPlanner.Api.Models;

public class MoneyAccount
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public MoneyOperationSource Source { get; set; }
    public string Currency { get; set; } = "RUB";
    public string? BankName { get; set; }
    public string? BankAccountNumber { get; set; }
    public string? BankBik { get; set; }
    public int? CompanyId { get; set; }
    [NotMapped]
    public string? CompanyName { get; internal set; }
    // Computed account-state eligibility, not a grant of endpoint permissions.
    [NotMapped]
    public bool CanUseForNewOperations { get; internal set; }
    [NotMapped]
    public bool CanEditExistingOperations { get; internal set; }
    [JsonIgnore]
    public LegalEntity? Company { get; set; }
    public int? BankConnectionAccountId { get; set; }
    [JsonIgnore]
    public BankConnectionAccount? BankConnectionAccount { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<MoneyOperation> MoneyOperations { get; set; } = new List<MoneyOperation>();
}
