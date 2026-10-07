namespace PayPlanner.Api.Models;

// Durable idempotency fence. Keep it after file cleanup so an old HTTP request
// or restarted worker cannot publish an attempt the actor has retired.
public sealed class CaseDocumentUploadRetirement
{
    public int CaseId { get; set; }
    public int ActorId { get; set; }
    public Guid UploadKey { get; set; }
    // Accepted generation can be retired before a physical object exists.
    public Guid? StorageObjectId { get; set; }
    public DateTime RetiredAt { get; set; }
}
