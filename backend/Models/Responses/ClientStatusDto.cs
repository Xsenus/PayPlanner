using System;

namespace PayPlanner.Api.Models.Responses;

public class ClientStatusDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ColorHex { get; set; } = "#2563EB";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}
