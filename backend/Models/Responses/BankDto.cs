namespace PayPlanner.Api.Models.Responses;

public sealed class BankDto
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Bik { get; init; }
    public string? CorrespondentAccount { get; init; }
    public string? City { get; init; }
    public bool IsForeign { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
