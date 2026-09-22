using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models;

public static class StorageReplicaStatuses
{
    public const string Pending = "pending";
    public const string Available = "available";
    public const string Suspect = "suspect";
    public const string Missing = "missing";
    public const string Corrupt = "corrupt";
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
