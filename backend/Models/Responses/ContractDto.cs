using System;
using System.Collections.Generic;

namespace PayPlanner.Api.Models.Responses;

/// <summary>
/// DTO договора с расширенной информацией по клиентам.
/// </summary>
public class ContractDto
{
    public int Id { get; set; }

    public string Number { get; set; } = string.Empty;

    public string? Title { get; set; }

    public DateTime Date { get; set; }

    public string? Description { get; set; }

    public decimal? Amount { get; set; }

    public DateTime? ValidUntil { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public IReadOnlyCollection<ContractClientDto> Clients { get; set; } = Array.Empty<ContractClientDto>();
}

/// <summary>
/// DTO клиента в контексте договора.
/// </summary>
public class ContractClientDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Company { get; set; }

    public int? ClientStatusId { get; set; }

    public string? ClientStatusName { get; set; }

    public string? ClientStatusColorHex { get; set; }
}
