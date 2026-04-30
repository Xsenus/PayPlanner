namespace PayPlanner.Api.Models;

public static class BankStatementImportBatchStatus
{
    public const string Applied = "Applied";
    public const string AppliedWithErrors = "AppliedWithErrors";
    public const string RolledBack = "RolledBack";
    public const string RollbackBlocked = "RollbackBlocked";
}

public static class BankStatementImportRowStatus
{
    public const string Created = "Created";
    public const string Skipped = "Skipped";
    public const string Error = "Error";
    public const string RolledBack = "RolledBack";
    public const string RollbackSkipped = "RollbackSkipped";
}

public class BankStatementImportBatch
{
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Status { get; set; } = BankStatementImportBatchStatus.Applied;
    public int RowsRead { get; set; }
    public int CreatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public int RollbackRows { get; set; }
    public string? ErrorSummary { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RolledBackAt { get; set; }
    public ICollection<BankStatementImportRow> Rows { get; set; } = new List<BankStatementImportRow>();
}

public class BankStatementImportRow
{
    public int Id { get; set; }
    public int BatchId { get; set; }
    public BankStatementImportBatch? Batch { get; set; }
    public int RowNumber { get; set; }
    public string ImportKey { get; set; } = string.Empty;
    public string Status { get; set; } = BankStatementImportRowStatus.Error;
    public string? Message { get; set; }
    public int? MoneyOperationId { get; set; }
    public MoneyOperation? MoneyOperation { get; set; }
    public DateTime Date { get; set; }
    public MoneyOperationDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string? AccountName { get; set; }
    public string? CounterpartyName { get; set; }
    public string? Purpose { get; set; }
    public string? PaymentOrderNumber { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RolledBackAt { get; set; }
}
