using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class StorageReplicaStatuses
{
    public const string Pending = "pending";
    public const string Available = "available";
    public const string Suspect = "suspect";
    public const string Missing = "missing";
    public const string Corrupt = "corrupt";
    public const string RepairPending = "repairPending";
    public const string RetainedCorrupt = "retainedCorrupt";
    public const string DeletePending = "deletePending";
    public const string Deleted = "deleted";
}

public static class StorageOperationStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Unknown = "unknown";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string DeadLetter = "deadLetter";
}

public static class StorageOperationTypes
{
    public const string Replicate = "replicate";
    public const string Verify = "verify";
    public const string Delete = "delete";
    public const string Repair = "repair";
    public const string PublishTaskFile = "publishTaskFile";
    public const string PublishCaseDocument = "publishCaseDocument";
    public const string NotifyTaskFilePublication = "notifyTaskFilePublication";
}

public static class StorageCapacityReservationStatuses
{
    public const string Pending = "pending";
    public const string Released = "released";
}

// The singleton version changes whenever a participating reservation is created
// or released. A capacity observation is valid only against that same epoch.
public sealed class StorageCapacityEpoch
{
    public int Id { get; set; } = 1;
    public long Version { get; set; }
}

// One idempotent upload reserves one row per physical resource. Pending rows are
// durable debt; a crash must not silently free space that might contain bytes.
public sealed class StorageCapacityReservation
{
    [MaxLength(160)] public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(512)] public string ResourceKey { get; set; } = string.Empty;
    [MaxLength(128)] public string GroupId { get; set; } = string.Empty;
    [MaxLength(64)] public string BindingHash { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = StorageCapacityReservationStatuses.Pending;
    public long Bytes { get; set; }
    public Guid ObjectId { get; set; }
    // Only new staged admissions set this. A writer must atomically clear it
    // while holding the row lock before it can receive a write ticket.
    public bool StageWriterAdmissionPending { get; set; }
    // Separate from product intent and final-size confirmation. Null means no
    // durable file proof; recovery must never infer deletion authority from it.
    [MaxLength(1024)] public string? StageFileReceiptJson { get; set; }
    // Captured from the original CreateNew marker descriptor, never backfilled from a path.
    [MaxLength(160)] public string? StageWriterMarkerIdentity { get; set; }
    [MaxLength(160)] public string? StageRootIdentity { get; set; }
    [MaxLength(2048)] public string LocalLocator { get; set; } = string.Empty;
    // Only the local resource row carries the exact product intent. Its hash alone
    // cannot reconstruct metadata after a provider ACK is lost before catalog commit.
    [MaxLength(65536)] public string? ProductContextJson { get; set; }
    // Recovery of a trusted generated intent before there is a catalog object.
    // Keep its lease separate from capacity accounting and the later publication lease.
    public Guid? GeneratedRecoveryLeaseToken { get; set; }
    public DateTime? GeneratedRecoveryLeaseExpiresAt { get; set; }
    public DateTime GeneratedRecoveryNextAttemptAt { get; set; } = new(2000, 1, 1);
    public int GeneratedRecoveryAttemptCount { get; set; }
    [MaxLength(32)] public string? GeneratedRecoveryLastStatus { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt { get; set; }
}

public sealed class StorageObject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(64)] public string DataClass { get; set; } = string.Empty;
    [MaxLength(64)] public string OwnerType { get; set; } = string.Empty;
    [MaxLength(128)] public string OwnerId { get; set; } = string.Empty;
    [MaxLength(512)] public string LogicalName { get; set; } = string.Empty;
    [MaxLength(255)] public string ContentType { get; set; } = "application/octet-stream";
    public long CurrentGeneration { get; set; }
    public bool IsDeleted { get; set; }
    public long Version { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<StorageObjectGeneration> Generations { get; set; } = new List<StorageObjectGeneration>();
    public ICollection<StorageOperation> Operations { get; set; } = new List<StorageOperation>();
    public ICollection<StorageTombstone> Tombstones { get; set; } = new List<StorageTombstone>();
}

public sealed class StorageObjectGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StorageObjectId { get; set; }
    public StorageObject StorageObject { get; set; } = null!;
    public long Generation { get; set; }
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string Sha256 { get; set; } = string.Empty;
    [MaxLength(64)] public string PolicyVersion { get; set; } = "legacy-v1";
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<StorageReplica> Replicas { get; set; } = new List<StorageReplica>();
    public ICollection<StorageOperation> Operations { get; set; } = new List<StorageOperation>();
}

// Current product pointer; immutable bytes and older objects remain in the catalog.
public sealed class StorageProductBinding
{
    [MaxLength(64)] public string Source { get; set; } = string.Empty;
    public long SourceId { get; set; }
    public Guid StorageObjectId { get; set; }
    public StorageObject StorageObject { get; set; } = null!;
    public long Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class StorageReplica
{
    public long Id { get; set; }
    public Guid StorageObjectGenerationId { get; set; }
    public StorageObjectGeneration StorageObjectGeneration { get; set; } = null!;
    [MaxLength(64)] public string ProviderKey { get; set; } = string.Empty;
    [MaxLength(128)] public string? ConnectionId { get; set; }
    [MaxLength(2048)] public string Locator { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = StorageReplicaStatuses.Pending;
    public long SizeBytes { get; set; }
    [MaxLength(64)] public string? Sha256 { get; set; }
    [MaxLength(512)] public string? ProviderVersion { get; set; }
    [MaxLength(2000)] public string? LastError { get; set; }
    public DateTime? LastVerifiedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class StorageOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StorageObjectId { get; set; }
    public StorageObject StorageObject { get; set; } = null!;
    public Guid? StorageObjectGenerationId { get; set; }
    public StorageObjectGeneration? StorageObjectGeneration { get; set; }
    [MaxLength(32)] public string OperationType { get; set; } = string.Empty;
    [MaxLength(32)] public string Status { get; set; } = StorageOperationStatuses.Pending;
    [MaxLength(160)] public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(64)] public string? ProviderKey { get; set; }
    [MaxLength(128)] public string? ConnectionId { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    [MaxLength(128)] public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    public string? PayloadJson { get; set; }
    [MaxLength(2000)] public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public sealed class StorageTombstone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StorageObjectId { get; set; }
    public StorageObject StorageObject { get; set; } = null!;
    public long Generation { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    public bool LegalHold { get; set; }
    public DateTime? RetainUntil { get; set; }
    public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
    public int? DeletedByUserId { get; set; }
}
