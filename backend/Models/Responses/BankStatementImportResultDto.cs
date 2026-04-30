using PayPlanner.Api.Models;

namespace PayPlanner.Api.Models.Responses;

public sealed class BankStatementImportResultDto
{
    public int? BatchId { get; set; }
    public int RowsRead { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<BankStatementImportCreatedOperationDto> Operations { get; set; } = new();
}

public class BankStatementImportBatchListItemDto
{
    public int Id { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int RowsRead { get; init; }
    public int CreatedRows { get; init; }
    public int SkippedRows { get; init; }
    public int ErrorRows { get; init; }
    public int RollbackRows { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? RolledBackAt { get; init; }
}

public sealed class BankStatementImportBatchDetailDto : BankStatementImportBatchListItemDto
{
    public string FileHash { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public string? ErrorSummary { get; init; }
    public IReadOnlyList<BankStatementImportRowDto> Rows { get; init; } = Array.Empty<BankStatementImportRowDto>();
}

public sealed class BankStatementImportRowDto
{
    public int Id { get; init; }
    public int RowNumber { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Message { get; init; }
    public int? MoneyOperationId { get; init; }
    public DateTime Date { get; init; }
    public MoneyOperationDirection Direction { get; init; }
    public decimal Amount { get; init; }
    public string? AccountName { get; init; }
    public string? CounterpartyName { get; init; }
    public string? Purpose { get; init; }
    public string? PaymentOrderNumber { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? RolledBackAt { get; init; }
}

public sealed class BankStatementImportRollbackResultDto
{
    public int BatchId { get; init; }
    public string Status { get; init; } = string.Empty;
    public int DeletedOperations { get; init; }
    public int SkippedRows { get; init; }
    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();
}

public sealed class BankStatementImportCreatedOperationDto
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public MoneyOperationDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string? CounterpartyName { get; set; }
    public string? PaymentOrderNumber { get; set; }
    public MoneyOperationMatchStatus MatchStatus { get; set; }
}
