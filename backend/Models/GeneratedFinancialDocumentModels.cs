namespace PayPlanner.Api.Models;

// Internal accepted attempts are not financial records. Their immutable proof
// points to separately stored bytes, without putting a financial graph in queue payloads.
public sealed class GeneratedFinancialDocumentAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UploadKey { get; set; }
    // Database-computed canonical key for indexed discovery; never client input.
    public string UploadKeyText { get; set; } = string.Empty;
    public int ClientCaseId { get; set; }
    public int WorkOrderId { get; set; }
    public int ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int EntityId { get; set; }
    public int? InvoiceEntityId { get; set; }
    public string RequestSha256 { get; set; } = string.Empty;
    public string FrozenDomainJson { get; set; } = string.Empty;
    public string FrozenDomainSha256 { get; set; } = string.Empty;
    public long ContentSizeBytes { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    // Historical publication marker, deliberately not cleared by document deletion.
    public int? PublishedDocumentId { get; set; }
    // A separate lease covers the gap before there is a physical storage object.
    public Guid? RecoveryLeaseToken { get; set; }
    public DateTime? RecoveryLeaseExpiresAt { get; set; }
    public DateTime RecoveryNextAttemptAt { get; set; } = new(2000, 1, 1);
    public int RecoveryAttemptCount { get; set; }
    public string? RecoveryLastStatus { get; set; }
}

public sealed class GeneratedFinancialNumberScope
{
    public int Id { get; set; }
    public long Version { get; set; }
}

public sealed class GeneratedFinancialNumberClaim
{
    public Guid AttemptId { get; set; }
    public GeneratedFinancialDocumentAttempt Attempt { get; set; } = null!;
    public string Kind { get; set; } = string.Empty;
    public string DateKey { get; set; } = string.Empty;
    public string Number { get; set; } = string.Empty;
}

public sealed class GeneratedFinancialActLineClaim
{
    public Guid AttemptId { get; set; }
    public GeneratedFinancialDocumentAttempt Attempt { get; set; } = null!;
    public int WorkOrderItemId { get; set; }
    public string SourceKey { get; set; } = string.Empty;
}

public sealed class GeneratedFinancialDocumentContent
{
    public Guid AttemptId { get; set; }
    public GeneratedFinancialDocumentAttempt Attempt { get; set; } = null!;
    public byte[] Bytes { get; set; } = [];
}
