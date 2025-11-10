namespace PayPlanner.Api.Models;

/// <summary>
/// Связующая сущность для отношений многие-ко-многим между клиентами и договорами.
/// </summary>
public class ClientContract
{
    /// <summary>
    /// Идентификатор клиента.
    /// </summary>
    public int ClientId { get; set; }

    /// <summary>
    /// Навигационное свойство клиента.
    /// </summary>
    public Client Client { get; set; } = null!;

    /// <summary>
    /// Идентификатор договора.
    /// </summary>
    public int ContractId { get; set; }

    /// <summary>
    /// Навигационное свойство договора.
    /// </summary>
    public Contract Contract { get; set; } = null!;
}
