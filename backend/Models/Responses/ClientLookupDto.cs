namespace PayPlanner.Api.Models.Responses;

public class ClientLookupDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Company { get; set; }
    public bool IsActive { get; set; }
}
