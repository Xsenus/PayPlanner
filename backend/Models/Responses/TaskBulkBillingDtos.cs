namespace PayPlanner.Api.Models.Responses;

public sealed class TaskBulkBillingResultDto
{
    public int Requested { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Skipped { get; set; }
    public int Conflicted { get; set; }
    public IReadOnlyList<TaskBulkBillingRowResultDto> Items { get; set; } = Array.Empty<TaskBulkBillingRowResultDto>();
}

public sealed class TaskBulkBillingRowResultDto
{
    public int Id { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? UpdatedAt { get; set; }
}
