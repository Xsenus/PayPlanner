namespace PayPlanner.Api.Models.Responses;

public class DatabaseStatusDto
{
    public string Provider { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool SupportsSqliteMaintenance { get; init; }
    public bool SupportsSqliteFileImport { get; init; }
}
