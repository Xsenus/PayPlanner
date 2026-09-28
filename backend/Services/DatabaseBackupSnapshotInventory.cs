using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupStoredFileReference(
    string SourceKind, int SourceId, string StoredPath, long? SizeBytes,
    string? Sha256, int? Version, int? ParentId);

/// <summary>One candidate replica, or an explicit row with ReplicaId=null when a generation has none.</summary>
public sealed record DatabaseBackupStorageReplicaReference(
    Guid GenerationId, Guid ObjectId, long Generation, long SizeBytes, string Sha256,
    bool ObjectIsDeleted, bool LegalHold, DateTime? RetainUntil,
    long? ReplicaId, string? ProviderKey, string? ConnectionId, string? Locator, string? ReplicaStatus);

/// <summary>
/// Streams references from the same read-only session that owns the database snapshot.
/// Does not resolve working paths or claim that any candidate's bytes have been backed up.
/// </summary>
public static class DatabaseBackupSnapshotInventory
{
    private static readonly (string Kind, string Sql)[] StoredFileQueries =
    [
        ("task-file", """
            SELECT "Id", "StoragePath", "SizeBytes", NULL, "Version", NULL
            FROM "TaskJournalFiles" ORDER BY "Id"
            """),
        ("case-document", """
            SELECT "Id", "StoragePath", "SizeBytes", NULL, "Version", NULL
            FROM "ClientCaseDocuments" ORDER BY "Id"
            """),
        ("chat-attachment", """
            SELECT a."Id", a."StoredFileName", a."Size", NULL, NULL, m."ThreadId"
            FROM "InternalChatAttachments" a LEFT JOIN "InternalChatMessages" m ON m."Id" = a."MessageId"
            ORDER BY a."Id"
            """),
        ("messenger-delivery", """
            SELECT "Id", "SnapshotPath", "SizeBytes", "ChecksumSha256", "DocumentVersion", NULL
            FROM "MessengerDocumentDeliveries" ORDER BY "Id"
            """),
        ("esign-original", """
            SELECT "Id", "SnapshotPath", "SizeBytes", "ChecksumSha256", "DocumentVersion", NULL
            FROM "ElectronicSignatureEnvelopes" ORDER BY "Id"
            """),
        ("esign-artifact", """
            SELECT "Id", "StoragePath", "SizeBytes", "ChecksumSha256", NULL, "EnvelopeId"
            FROM "ElectronicSignatureArtifacts" ORDER BY "Id"
            """),
        ("profile-photo", """
            SELECT "Id", "PhotoUrl", NULL, NULL, NULL, NULL
            FROM "Users" WHERE "PhotoUrl" IS NOT NULL AND "PhotoUrl" <> '' ORDER BY "Id"
            """),
    ];

    public static async IAsyncEnumerable<DatabaseBackupStoredFileReference> ReadStoredFilesAsync(
        DatabaseBackupSnapshotSession snapshot, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var (kind, sql) in StoredFileQueries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = snapshot.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetInt32(0);
                var path = reader.IsDBNull(1) ? null : reader.GetString(1);
                var size = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);
                var hash = reader.IsDBNull(3) ? null : reader.GetString(3);
                var version = reader.IsDBNull(4) ? (int?)null : reader.GetInt32(4);
                var parent = reader.IsDBNull(5) ? (int?)null : reader.GetInt32(5);
                if (string.IsNullOrWhiteSpace(path) || size is < 0 || kind == "chat-attachment" && parent is null)
                    throw new InvalidDataException($"Некорректная файловая ссылка в снимке: {kind} #{id.ToString(CultureInfo.InvariantCulture)}.");
                yield return new(kind, id, path, size, hash, version, parent);
            }
        }
    }

    public static async IAsyncEnumerable<DatabaseBackupStorageReplicaReference> ReadStorageReplicasAsync(
        DatabaseBackupSnapshotSession snapshot, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await using (var current = snapshot.CreateCommand("""
            SELECT o."Id" FROM "StorageObjects" o
            WHERE NOT EXISTS (
                SELECT 1 FROM "StorageObjectGenerations" g
                WHERE g."StorageObjectId" = o."Id" AND g."Generation" = o."CurrentGeneration")
            LIMIT 1
            """))
        {
            if (await current.ExecuteScalarAsync(cancellationToken) is not null)
                throw new InvalidDataException("Объект хранилища в снимке не имеет текущего поколения.");
        }
        // Deletion is object-wide: all generations can be retained by the object's legal hold.
        // Aggregate tombstones before the replica join, avoiding duplicate rows and losing older holds.
        await using var command = snapshot.CreateCommand("""
            SELECT g."Id", g."StorageObjectId", g."Generation", g."SizeBytes", g."Sha256",
                   o."IsDeleted", o."Id", t."Held", t."RetainUntil",
                   r."Id", r."ProviderKey", r."ConnectionId", r."Locator", r."Status"
            FROM "StorageObjectGenerations" g
            LEFT JOIN "StorageObjects" o ON o."Id" = g."StorageObjectId"
            LEFT JOIN (
                SELECT "StorageObjectId", MAX(CASE WHEN "LegalHold" THEN 1 ELSE 0 END) AS "Held",
                       MAX("RetainUntil") AS "RetainUntil"
                FROM "StorageTombstones" GROUP BY "StorageObjectId"
            ) t ON t."StorageObjectId" = g."StorageObjectId"
            LEFT JOIN "StorageReplicas" r ON r."StorageObjectGenerationId" = g."Id"
            ORDER BY g."Id", r."Id"
            """);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generationId = reader.GetGuid(0);
            var objectId = reader.GetGuid(1);
            var generation = reader.GetInt64(2);
            var size = reader.GetInt64(3);
            var hash = reader.GetString(4);
            var deleted = reader.IsDBNull(5) ? (bool?)null : reader.GetBoolean(5);
            if (reader.IsDBNull(6) || deleted is null || size < 0 || generation < 1)
                throw new InvalidDataException($"Некорректное поколение хранилища в снимке: {generationId:D}.");
            var held = !reader.IsDBNull(7) && reader.GetInt32(7) != 0;
            var retainUntil = reader.IsDBNull(8) ? (DateTime?)null : reader.GetDateTime(8);
            var replicaId = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
            var provider = reader.IsDBNull(10) ? null : reader.GetString(10);
            var connection = reader.IsDBNull(11) ? null : reader.GetString(11);
            var locator = reader.IsDBNull(12) ? null : reader.GetString(12);
            var status = reader.IsDBNull(13) ? null : reader.GetString(13);
            yield return new(generationId, objectId, generation, size, hash, deleted.Value, held, retainUntil,
                replicaId, provider, connection, locator, status);
        }
    }
}
