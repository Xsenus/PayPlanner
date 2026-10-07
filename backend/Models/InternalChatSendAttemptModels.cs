namespace PayPlanner.Api.Models;

// Metadata capture is not a published message or an acknowledgement that bytes
// are durable. AcceptedAt is set only after every original file is recoverable.
public sealed class InternalChatSendAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ThreadId { get; set; }
    public int ActorId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string Text { get; set; } = string.Empty;
    public int? ReplyToMessageId { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public string RequestSha256 { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
    // Historical marker survives message deletion; it must never enable resend.
    public int? PublishedMessageId { get; set; }
    public long Version { get; set; } = 1;
    public Guid? RecoveryLeaseToken { get; set; }
    public DateTime? RecoveryLeaseExpiresAt { get; set; }
    public DateTime RecoveryNextAttemptAt { get; set; } = new(2000, 1, 1);
    public int RecoveryAttemptCount { get; set; }
    public string? RecoveryLastStatus { get; set; }
    public ICollection<InternalChatSendAttemptFile> Files { get; set; } = new List<InternalChatSendAttemptFile>();
    public ICollection<InternalChatSendAttemptMention> Mentions { get; set; } = new List<InternalChatSendAttemptMention>();
}

public sealed class InternalChatSendAttemptFile
{
    public Guid AttemptId { get; set; }
    public InternalChatSendAttempt Attempt { get; set; } = null!;
    public int Index { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    // A catalog pointer is a separate, mutable receipt. Readers must verify its
    // owner, immutable generation and bytes against the frozen request proof.
    public Guid? StorageObjectId { get; set; }
    public long? StorageGeneration { get; set; }
}

public sealed class InternalChatSendAttemptMention
{
    public Guid AttemptId { get; set; }
    public InternalChatSendAttempt Attempt { get; set; } = null!;
    public int UserId { get; set; }
}
