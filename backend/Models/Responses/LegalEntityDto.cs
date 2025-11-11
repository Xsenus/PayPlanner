using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models.Responses;

public class LegalEntityClientDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsActive { get; set; }
    public int? ClientStatusId { get; set; }
    public string? ClientStatusName { get; set; }
    public string? ClientStatusColorHex { get; set; }
}

public class LegalEntityListItemDto
{
    public int Id { get; set; }
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
    public string? Notes { get; set; }
    public int ClientsCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<LegalEntityClientDto> Clients { get; set; } = new();
}

public class LegalEntityDetailDto : LegalEntityListItemDto
{
}

public class LegalEntitySuggestionDto
{
    public string ShortName { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public string? Inn { get; set; }
    public string? Kpp { get; set; }
    public string? Ogrn { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Director { get; set; }
}
