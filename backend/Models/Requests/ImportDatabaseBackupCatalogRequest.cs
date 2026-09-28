using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public sealed class DiscoverDatabaseBackupsRequest
{
    [MaxLength(600000)] public string? Cursor { get; init; }
    [Range(1, 20)] public int PageSize { get; init; } = 20;
}

public sealed class ImportDatabaseBackupCatalogRequest
{
    [Required, MinLength(1), MaxLength(20)]
    public List<string> RelativePaths { get; init; } = [];
    public bool DryRun { get; init; } = true;
}
