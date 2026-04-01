namespace PayPlanner.Api.Models.Responses;

public class DatabaseImportTableResultDto
{
    public string Table { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int SourceRows { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Conflicts { get; set; }
    public int Errors { get; set; }
    public bool Completed { get; set; }
    public string? Message { get; set; }
}

public class DatabaseImportResultDto
{
    public string SourceProvider { get; init; } = "sqlite";
    public string TargetProvider { get; init; } = string.Empty;
    public string TargetProviderDisplayName { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public bool Success { get; set; }
    public int SourceTablesProcessed { get; set; }
    public int TablesWithErrors { get; set; }
    public List<DatabaseImportTableResultDto> Tables { get; init; } = [];
}
