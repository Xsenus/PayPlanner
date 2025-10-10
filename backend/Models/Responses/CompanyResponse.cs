namespace PayPlanner.Api.Models.Responses;

/// <summary>
/// Ответ API для компании с привязанными клиентами.
/// </summary>
public class CompanyResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public ICollection<ClientSummaryResponse> Members { get; set; } = new List<ClientSummaryResponse>();
}

/// <summary>
/// Краткая информация о клиенте, связанном с компанией.
/// </summary>
public class ClientSummaryResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
