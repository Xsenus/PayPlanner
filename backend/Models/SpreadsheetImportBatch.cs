namespace PayPlanner.Api.Models;

public static class SpreadsheetImportBatchStatus
{
    public const string Applied = "Applied";
    public const string AppliedWithErrors = "AppliedWithErrors";
    public const string RolledBack = "RolledBack";
    public const string RolledBackWithConflicts = "RolledBackWithConflicts";
}

public static class SpreadsheetImportOperationAction
{
    public const string Created = "Created";
    public const string Updated = "Updated";
    public const string Skipped = "Skipped";
    public const string Error = "Error";
}

public static class SpreadsheetImportRollbackAction
{
    public const string Deleted = "Deleted";
    public const string Restored = "Restored";
    public const string Skipped = "Skipped";
    public const string Error = "Error";
}

public class SpreadsheetImportBatch
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = SpreadsheetImportBatchStatus.Applied;
    public int TotalRows { get; set; }
    public int CreatedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public int RollbackRows { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public DateTime? RolledBackAt { get; set; }
    public int? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public string? SummaryJson { get; set; }
    public ICollection<SpreadsheetImportBatchFile> Files { get; set; } = new List<SpreadsheetImportBatchFile>();
    public ICollection<SpreadsheetImportBatchOperation> Operations { get; set; } = new List<SpreadsheetImportBatchOperation>();
}

public class SpreadsheetImportBatchFile
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public SpreadsheetImportBatch? Batch { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public string DetectedKind { get; set; } = string.Empty;
    public string? SheetName { get; set; }
    public int RowCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SpreadsheetImportBatchOperation
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public SpreadsheetImportBatch? Batch { get; set; }
    public string SectionKey { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceFileHash { get; set; } = string.Empty;
    public string SourceRowId { get; set; } = string.Empty;
    public int SourceRowNumber { get; set; }
    public string SourceKey { get; set; } = string.Empty;
    public string SourceTitle { get; set; } = string.Empty;
    public string Action { get; set; } = SpreadsheetImportOperationAction.Error;
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string? Message { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public string? MetadataJson { get; set; }
    public string? RollbackAction { get; set; }
    public string? RollbackMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RolledBackAt { get; set; }
}
