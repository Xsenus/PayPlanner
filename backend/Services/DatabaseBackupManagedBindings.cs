using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;

namespace PayPlanner.Api.Services;

public static class DatabaseBackupManagedBindings
{
    private sealed record Header(string Kind, int FormatVersion, string InventorySha256);
    private sealed record Footer(string Kind, long BindingCount);
    private sealed record BindingLine(string Kind, DatabaseBackupManagedBinding Binding);
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static async Task<long> WriteAsync(Stream inventory, Stream destination, ZipArchive archive,
        string partPath, string inventorySha256, string stagingDirectory,
        Func<DatabaseBackupStorageReplicaReference, CancellationToken, Task<StorageReadResult>> openReplica,
        CancellationToken cancellationToken)
    {
        _ = DatabaseBackupArchiveHash.ToComment(inventorySha256);
        await DatabaseBackupNativeBindings.WriteLineAsync(destination, new Header("header", 1, inventorySha256), cancellationToken);
        var count = await DatabaseBackupManagedArchive.WriteAsync(inventory, archive, partPath, stagingDirectory,
            openReplica, async (binding, token) =>
            {
                Validate(binding);
                await DatabaseBackupNativeBindings.WriteLineAsync(destination, new BindingLine("binding", binding), token);
            }, cancellationToken);
        await DatabaseBackupNativeBindings.WriteLineAsync(destination, new Footer("footer", count), cancellationToken);
        return count;
    }

    public static async IAsyncEnumerable<DatabaseBackupManagedBinding> ReadAsync(Stream source, string inventorySha256,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var header = false;
        var footer = false;
        long count = 0;
        await foreach (var line in DatabaseBackupNativeBindings.ReadLinesAsync(source, cancellationToken))
        {
            var parsed = Parse(line);
            if (footer) throw Invalid();
            if (!header)
            {
                var value = parsed as Header ?? throw Invalid();
                if (value.FormatVersion != 1 || !string.Equals(value.InventorySha256, inventorySha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
                _ = DatabaseBackupArchiveHash.ToComment(value.InventorySha256);
                header = true;
                continue;
            }
            if (parsed is Footer ending)
            {
                if (ending.BindingCount != count) throw Invalid();
                footer = true;
                continue;
            }
            var binding = (parsed as BindingLine)?.Binding ?? throw Invalid();
            Validate(binding);
            count = checked(count + 1);
            yield return binding;
        }
        if (!header || !footer) throw Invalid();
    }

    public static async Task VerifyAsync(Stream inventory, Stream mappings, string inventorySha256,
        Func<string, string, CancellationToken, Task<DatabaseBackupArchiveFileHash?>> readArchivedFile,
        CancellationToken cancellationToken)
    {
        await using var mapping = ReadAsync(mappings, inventorySha256, cancellationToken).GetAsyncEnumerator(cancellationToken);
        DatabaseBackupStorageReplicaReference? generation = null;
        DatabaseBackupManagedBinding? selected = null;
        var found = false;
        long? previousReplicaId = null;
        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken))
        {
            if (row.Replica is not { } replica) continue;
            if (generation is null || generation.GenerationId != replica.GenerationId)
            {
                if (generation is not null && (!found
                    || string.CompareOrdinal(generation.GenerationId.ToString("D"), replica.GenerationId.ToString("D")) >= 0)) throw Invalid();
                if (!await mapping.MoveNextAsync()) throw Invalid();
                selected = mapping.Current;
                generation = replica;
                previousReplicaId = null;
                found = false;
                var actual = await readArchivedFile(selected.ArchivePartRelativePath, selected.ArchiveEntryPath, cancellationToken);
                if (actual is null || actual.SizeBytes != selected.ActualSizeBytes
                    || !string.Equals(actual.Sha256, selected.ActualSha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
            }
            else if (previousReplicaId is null || replica.ReplicaId is null || replica.ReplicaId <= previousReplicaId) throw Invalid();
            if (DatabaseBackupManagedArchive.GenerationFacts(selected!.SelectedReplica)
                != DatabaseBackupManagedArchive.GenerationFacts(replica)) throw Invalid();
            previousReplicaId = replica.ReplicaId;
            if (selected.SelectedReplica == replica) found = true;
        }
        if (generation is not null && !found || await mapping.MoveNextAsync()) throw Invalid();
    }

    private static void Validate(DatabaseBackupManagedBinding binding)
    {
        var reference = binding.SelectedReplica;
        if (reference is null || reference.GenerationId == Guid.Empty || reference.ObjectId == Guid.Empty
            || reference.Generation < 1 || reference.SizeBytes < 0 || reference.ReplicaId is null or < 1
            || reference.ReplicaStatus != StorageReplicaStatuses.Available
            || string.IsNullOrWhiteSpace(reference.ProviderKey) || string.IsNullOrWhiteSpace(reference.Locator)
            || binding.ActualSizeBytes != reference.SizeBytes
            || !string.Equals(binding.ActualSha256, reference.Sha256, StringComparison.OrdinalIgnoreCase)
            || binding.ArchiveEntryPath != $"managed-generations/{reference.GenerationId:D}.bin") throw Invalid();
        DatabaseBackupNativeBindings.ValidatePortablePath(binding.ArchivePartRelativePath);
        _ = DatabaseBackupArchiveHash.ToComment(binding.ActualSha256);
    }

    private static object Parse(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            DatabaseBackupNativeBindings.RejectDuplicateProperties(bytes.Span);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("kind", out var kind)) throw Invalid();
            return kind.GetString() switch
            {
                "header" => JsonSerializer.Deserialize<Header>(bytes.Span, Options) ?? throw Invalid(),
                "footer" => JsonSerializer.Deserialize<Footer>(bytes.Span, Options) ?? throw Invalid(),
                "binding" => JsonSerializer.Deserialize<BindingLine>(bytes.Span, Options) ?? throw Invalid(),
                _ => throw Invalid()
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new InvalidDataException("Некорректное сопоставление поколений резервной копии.", error); }
    }

    private static InvalidDataException Invalid() => new("Неполное или противоречивое сопоставление поколений резервной копии.");
}
