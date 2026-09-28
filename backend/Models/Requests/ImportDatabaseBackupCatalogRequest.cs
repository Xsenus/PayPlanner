using System.ComponentModel.DataAnnotations;

namespace PayPlanner.Api.Models.Requests;

public sealed class ImportDatabaseBackupCatalogRequest
{
    [Required, MinLength(1), MaxLength(20)]
    public List<string> RelativePaths { get; init; } = [];
    public bool DryRun { get; init; } = true;
}
