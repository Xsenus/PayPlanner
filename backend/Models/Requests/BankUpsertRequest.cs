using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public sealed class BankUpsertRequest
{
    [Required]
    [MaxLength(300)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(16)]
    public string? Bik { get; init; }

    [MaxLength(34)]
    public string? CorrespondentAccount { get; init; }

    [MaxLength(120)]
    public string? City { get; init; }

    public bool IsForeign { get; init; }
}
