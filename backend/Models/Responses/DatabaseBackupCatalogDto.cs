namespace PayPlanner.Api.Models.Responses;

public sealed class DatabaseBackupCatalogPageDto
{
    public List<DatabaseBackupCatalogItemDto> Items { get; init; } = [];
    public string? NextCursor { get; init; }
}

public sealed class DatabaseBackupCatalogItemDto
{
    public string Id { get; init; } = string.Empty;
    public string BackupRelativePath { get; init; } = string.Empty;
    public string DatabaseProvider { get; init; } = string.Empty;
    public string BackupFormat { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public List<DatabaseBackupCatalogCopyDto> Copies { get; init; } = [];
}

public sealed class DatabaseBackupCatalogCopyDto
{
    public string ProviderKey { get; init; } = string.Empty;
    public string ConnectionId { get; init; } = string.Empty;
    public string Locator { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int AttemptCount { get; init; }
    public string? LastError { get; init; }
    public DateTime? LastVerifiedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
