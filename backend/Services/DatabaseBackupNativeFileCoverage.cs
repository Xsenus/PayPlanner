using System.IO.Compression;

namespace PayPlanner.Api.Services;

/// <summary>Checks native snapshot references against the entries produced by this backup operation.</summary>
public static class DatabaseBackupNativeFileCoverage
{
    public static async Task ValidateAsync(Stream inventory, ZipArchive? archive,
        DatabaseBackupFileBindingResolver resolver, CancellationToken cancellationToken)
    {
        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken))
        {
            if (row.StoredFile is not { } reference) continue;
            var resolved = resolver.Resolve(reference);
            if (resolved.ExternalReference) continue;
            var entry = archive?.GetEntry(resolved.ArchiveEntryPath!);
            if (entry is null || string.IsNullOrEmpty(entry.Name))
                throw new InvalidDataException($"Документ снимка отсутствует в файловом архиве: {reference.SourceKind} #{reference.SourceId}.");
            var hash = DatabaseBackupArchiveHash.FromComment(entry.Comment);
            if (reference.SizeBytes is { } size && size != entry.Length
                || reference.Sha256 is { } expected && !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Архивный документ не соответствует размеру или SHA-256 снимка: {reference.SourceKind} #{reference.SourceId}.");
        }
    }
}
