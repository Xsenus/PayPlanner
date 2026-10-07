namespace PayPlanner.Api.Models;

// Accepted court evidence is independent of printable-document readiness.
// Attempts retain their original actor and frozen metadata across later reads.
public sealed class CourtDocumentSourceScope
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ClientCaseId { get; set; }
    public string BindingUrl { get; set; } = string.Empty;
    public string BindingSha256 { get; set; } = string.Empty;
    public long Version { get; set; } = 1;
    public string InputSha256 { get; set; } = string.Empty;
    public DateTime LastAcceptedAt { get; set; }
    public int LastActorId { get; set; }
    public string LastActorName { get; set; } = string.Empty;
}

public sealed class CourtDocumentSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ScopeId { get; set; }
    public CourtDocumentSourceScope Scope { get; set; } = null!;
    public int ClientCaseId { get; set; }
    public string DocumentKey { get; set; } = string.Empty;
    public long AcceptedScopeVersion { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public int CurrentAttemptVersion { get; set; } = 1;
}

public sealed class CourtDocumentSourceAttempt
{
    // Also the managed upload key; allocated once by the trusted source registry.
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public CourtDocumentSource Source { get; set; } = null!;
    public int Version { get; set; } = 1;
    public int ActorId { get; set; }
    public string ActorName { get; set; } = string.Empty;
    public string FrozenSnapshotJson { get; set; } = string.Empty;
    public string FrozenSnapshotSha256 { get; set; } = string.Empty;
    public string RequestSha256 { get; set; } = string.Empty;
}

public sealed class CourtDocumentSourceContent
{
    public Guid AttemptId { get; set; }
    public CourtDocumentSourceAttempt Attempt { get; set; } = null!;
    public byte[] Bytes { get; set; } = [];
    public long SizeBytes { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}
