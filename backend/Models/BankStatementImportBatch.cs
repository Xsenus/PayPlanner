using System.Text.Json.Serialization;

namespace PayPlanner.Api.Models;

public static class BankStatementImportSources
{
    public const string File = "file";
    public const string ManualBank = "manual-bank";
    public const string BackgroundBank = "background-bank";
}

public static class BankStatementImportTriggers
{
    public const string Manual = "manual";
    public const string Folder = "folder";
    public const string Background = "background";
}

public static class BankStatementImportBatchStatus
{
    public const string Loading = "Loading";
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
    public const string ClarificationRequired = "ClarificationRequired";
    public const string Pending = "Pending";
}

public class BankStatementImportBatch
{
    public int Id { get; set; }
    // Nullable for historical packages whose origin has not been confirmed.
    public int? CompanyId { get; set; }
    [JsonIgnore]
    public LegalEntity? Company { get; set; }
    public int? MoneyAccountId { get; set; }
    [JsonIgnore]
    public MoneyAccount? MoneyAccount { get; set; }
    public int? BankConnectionAccountId { get; set; }
    [JsonIgnore]
    public BankConnectionAccount? BankConnectionAccount { get; set; }
    // Durable actor IDs follow the audit-record pattern: deleting or merging a
    // user must not replace the historical initiator with another employee.
    public int? ImportedByUserId { get; set; }
    public int? ResponsibleUserId { get; set; }
    public string? Source { get; set; }
    public string? Trigger { get; set; }
    public int? ParentBatchId { get; set; }
    [JsonIgnore]
    public BankStatementImportBatch? ParentBatch { get; set; }
    [JsonIgnore]
    public string? PackageKey { get; set; }
    public DateTime? PeriodFrom { get; set; }
    public DateTime? PeriodTo { get; set; }
    public DateTime? CompletedAt { get; set; }
    [JsonIgnore]
    public string? SourceResultJson { get; set; }
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
    // Null on legacy rows; new producer pages retain one row per source identity.
    [JsonIgnore]
    public string? RowIdentityKey { get; set; }
    public string Status { get; set; } = BankStatementImportRowStatus.Error;
    public string? Message { get; set; }
    public int? MoneyOperationId { get; set; }
    public MoneyOperation? MoneyOperation { get; set; }
    public int? BankStatementOperationId { get; set; }
    [JsonIgnore]
    public BankStatementOperation? BankStatementOperation { get; set; }
    public int? OriginRowId { get; set; }
    [JsonIgnore]
    public BankStatementImportRow? OriginRow { get; set; }
    public string? ExternalOperationId { get; set; }
    [JsonIgnore]
    public string? MovementKey { get; set; }
    public string? Currency { get; set; }
    public bool? IsBooked { get; set; }
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
