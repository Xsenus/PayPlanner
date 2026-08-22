namespace PayPlanner.Api.Models;

/// <summary>
/// Явная связь договора с делом клиента.
/// </summary>
public sealed class ClientCaseContract
{
    public int ClientCaseId { get; set; }
    public ClientCase ClientCase { get; set; } = null!;

    public int ContractId { get; set; }
    public Contract Contract { get; set; } = null!;
}
