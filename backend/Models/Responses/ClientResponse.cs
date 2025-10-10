using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

/// <summary>
/// Ответ API для клиента с дополнительной информацией о компаниях.
/// </summary>
public class ClientResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool IsActive { get; set; }
    public ICollection<ClientCase> Cases { get; set; } = new List<ClientCase>();
    public ICollection<CompanySummaryResponse> Companies { get; set; } = new List<CompanySummaryResponse>();
}

/// <summary>
/// Краткая информация о компании, связанной с клиентом.
/// </summary>
public class CompanySummaryResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}
