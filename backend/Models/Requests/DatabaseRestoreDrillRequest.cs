namespace PayPlanner.Api.Models.Requests;

public sealed class DatabaseRestoreDrillRequest
{
    public string RelativePath { get; set; } = string.Empty;
    public bool ExecutePostgreSql { get; set; }
    public string? ConfirmationToken { get; set; }
}
