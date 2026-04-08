namespace PayPlanner.Api.Models.Responses;

public class YealinkContactImportResultDto
{
    public bool DryRun { get; set; }
    public int Scanned { get; set; }
    public int WithPhones { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int SkippedExisting { get; set; }
    public int SkippedNoPhone { get; set; }
    public int SkippedDuplicateInFile { get; set; }
    public int SkippedByUser { get; set; }
    public int SkippedInvalid { get; set; }
    public List<YealinkContactImportDetailDto> Details { get; set; } = new();
}

public class YealinkContactImportDetailDto
{
    public int RowId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Group { get; set; }
    public List<string> Phones { get; set; } = new();
    public string Action { get; set; } = string.Empty;
    public string? MatchedSourceType { get; set; }
    public int? MatchedSourceId { get; set; }
    public string? MatchedName { get; set; }
    public List<string> MatchedPhones { get; set; } = new();
    public string? Message { get; set; }
}
