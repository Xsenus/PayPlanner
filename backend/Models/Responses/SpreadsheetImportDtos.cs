namespace PayPlanner.Api.Models.Responses;

public class SpreadsheetImportPreviewDto
{
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public int TotalSelectableRows { get; set; }
    public List<SpreadsheetImportPreviewFileDto> Files { get; set; } = new();
    public List<SpreadsheetImportPreviewSectionDto> Sections { get; set; } = new();
    public List<string> Messages { get; set; } = new();
}

public class SpreadsheetImportPreviewFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string DetectedKind { get; set; } = string.Empty;
    public string DetectedLabel { get; set; } = string.Empty;
    public string? SheetName { get; set; }
    public int RowCount { get; set; }
    public string? Message { get; set; }
}

public class SpreadsheetImportPreviewSectionDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int CreateRows { get; set; }
    public int UpdateRows { get; set; }
    public int SkipRows { get; set; }
    public int ErrorRows { get; set; }
    public int SelectableRows { get; set; }
    public List<SpreadsheetImportPreviewRowDto> Rows { get; set; } = new();
}

public class SpreadsheetImportPreviewRowDto
{
    public string PreviewId { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFileHash { get; set; } = string.Empty;
    public int SourceRowNumber { get; set; }
    public string Action { get; set; } = string.Empty;
    public bool IsSelectable { get; set; }
    public bool IsDerived { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? Message { get; set; }
    public List<string> DependencyPreviewIds { get; set; } = new();
    public List<string> DependencyTitles { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public string? CounterpartyImportMode { get; set; }
    public string? CounterpartyImportModeHint { get; set; }
    public List<string> AvailableCounterpartyImportModes { get; set; } = new();
    public List<SpreadsheetImportPreviewChangeDto> Changes { get; set; } = new();
    public List<SpreadsheetImportPreviewCellDto> Cells { get; set; } = new();
}

public class SpreadsheetImportPreviewChangeDto
{
    public string FieldKey { get; set; } = string.Empty;
    public string FieldLabel { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? CurrentValue { get; set; }
    public string? IncomingValue { get; set; }
}

public class SpreadsheetImportPreviewCellDto
{
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
}

public class SpreadsheetImportBatchSummaryDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int CreatedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public int RollbackRows { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime? RolledBackAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public List<SpreadsheetImportBatchFileDto> Files { get; set; } = new();
}

public class SpreadsheetImportBatchDetailDto : SpreadsheetImportBatchSummaryDto
{
    public List<SpreadsheetImportBatchOperationDto> Operations { get; set; } = new();
    public int OperationsTotal { get; set; }
    public int OperationsPage { get; set; } = 1;
    public int OperationsPageSize { get; set; } = 25;
}

public class SpreadsheetImportBatchFileDto
{
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string DetectedKind { get; set; } = string.Empty;
    public string? SheetName { get; set; }
    public int RowCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class SpreadsheetImportBatchOperationDto
{
    public int Id { get; set; }
    public string SectionKey { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFileHash { get; set; } = string.Empty;
    public string SourceRowId { get; set; } = string.Empty;
    public int SourceRowNumber { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string SourceTitle { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string? Message { get; set; }
    public string? MetadataJson { get; set; }
    public string? RollbackAction { get; set; }
    public string? RollbackMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RolledBackAt { get; set; }
}
