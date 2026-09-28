using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupInventoryRow(
    DatabaseBackupStoredFileReference? StoredFile, DatabaseBackupStorageReplicaReference? Replica);

/// <summary>Reads the existing inventory format with bounded memory. Footer validation requires full enumeration.</summary>
public static class DatabaseBackupInventoryReader
{
    private const int BufferLimit = 65536;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 16
    };

    public static async IAsyncEnumerable<DatabaseBackupInventoryRow> ReadAsync(Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferLimit);
        var schema = new Schema();
        var state = new JsonReaderState(new JsonReaderOptions { MaxDepth = 16 });
        var count = 0;
        var final = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = Parse(buffer.AsSpan(0, count), final, state, schema);
                state = result.State;
                count -= result.Consumed;
                if (count != 0 && result.Consumed != 0)
                    Buffer.BlockCopy(buffer, result.Consumed, buffer, 0, count);
                if (result.Row is not null) { yield return result.Row; continue; }
                if (final) { schema.Validate(); yield break; }
                if (count == BufferLimit)
                    throw new InvalidDataException("Запись перечня документов превышает допустимый размер.");
                var read = await source.ReadAsync(buffer.AsMemory(count, BufferLimit - count), cancellationToken);
                count += read;
                final = read == 0;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static ParseResult Parse(ReadOnlySpan<byte> bytes, bool final, JsonReaderState state, Schema schema)
    {
        try { return ParseCore(bytes, final, state, schema); }
        catch (InvalidOperationException error)
        { throw new InvalidDataException("Некорректное текстовое значение перечня документов.", error); }
    }

    private static ParseResult ParseCore(ReadOnlySpan<byte> bytes, bool final, JsonReaderState state, Schema schema)
    {
        var reader = new Utf8JsonReader(bytes, final, state);
        while (true)
        {
            var before = reader;
            if (!reader.Read()) return new((int)reader.BytesConsumed, reader.CurrentState, null);
            if (schema.Ended) throw Invalid();
            if (!schema.Started)
            {
                if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 0) throw Invalid();
                schema.Started = true;
                continue;
            }
            if (schema.Array is not null)
            {
                if (reader.TokenType == JsonTokenType.EndArray && reader.CurrentDepth == 1)
                { schema.Array = null; continue; }
                if (reader.TokenType != JsonTokenType.StartObject || reader.CurrentDepth != 2) throw Invalid();
                var recordReader = reader;
                if (!reader.TrySkip()) return new((int)before.BytesConsumed, before.CurrentState, null);
                var fieldsReader = recordReader;
                schema.RecordFields.Clear();
                var depth = fieldsReader.CurrentDepth;
                while (fieldsReader.Read())
                {
                    if (fieldsReader.TokenType == JsonTokenType.EndObject && fieldsReader.CurrentDepth == depth) break;
                    if (fieldsReader.TokenType == JsonTokenType.PropertyName && fieldsReader.CurrentDepth == depth + 1
                        && (!schema.RecordFields.Add(fieldsReader.GetString()!) || schema.RecordFields.Count > 16)) throw Invalid();
                }
                DatabaseBackupInventoryRow row;
                if (schema.Array == "storedFiles")
                {
                    var item = JsonSerializer.Deserialize<DatabaseBackupStoredFileReference>(ref recordReader, Options) ?? throw Invalid();
                    if (item.SourceId <= 0 || string.IsNullOrWhiteSpace(item.StoredPath)
                        || item.SizeBytes is < 0 || item.Version is < 1 || item.ParentId is < 1
                        || item.SourceKind is not ("task-file" or "case-document" or "chat-attachment"
                            or "messenger-delivery" or "esign-original" or "esign-artifact" or "profile-photo")
                        || item.SourceKind == "chat-attachment" && item.ParentId is null)
                        throw Invalid();
                    if (item.Sha256 is not null) _ = DatabaseBackupArchiveHash.ToComment(item.Sha256);
                    schema.Files = checked(schema.Files + 1);
                    row = new(item, null);
                }
                else
                {
                    var item = JsonSerializer.Deserialize<DatabaseBackupStorageReplicaReference>(ref recordReader, Options) ?? throw Invalid();
                    if (item.GenerationId == Guid.Empty || item.ObjectId == Guid.Empty || item.Generation < 1 || item.SizeBytes < 0
                        || item.ReplicaId is < 1 || item.ReplicaId is not null
                            && (string.IsNullOrWhiteSpace(item.ProviderKey) || string.IsNullOrWhiteSpace(item.Locator)
                                || string.IsNullOrWhiteSpace(item.ReplicaStatus))
                        || item.ReplicaId is null && (item.ProviderKey is not null || item.ConnectionId is not null
                            || item.Locator is not null || item.ReplicaStatus is not null)) throw Invalid();
                    _ = DatabaseBackupArchiveHash.ToComment(item.Sha256);
                    schema.Replicas = checked(schema.Replicas + 1);
                    row = new(null, item);
                }
                return new((int)reader.BytesConsumed, reader.CurrentState, row);
            }
            if (schema.Pending is not null)
            {
                var name = schema.Pending;
                schema.Pending = null;
                if (name is "storedFiles" or "storageReplicas")
                {
                    if (reader.TokenType != JsonTokenType.StartArray || reader.CurrentDepth != 1) throw Invalid();
                    schema.Array = name;
                }
                else
                {
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value) || value < 0) throw Invalid();
                    switch (name)
                    {
                        case "formatVersion": if (value != 1) throw Invalid(); break;
                        case "storedFileCount": schema.ExpectedFiles = value; break;
                        case "storageReplicaCount": schema.ExpectedReplicas = value; break;
                    }
                }
                continue;
            }
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1)
            {
                var name = reader.GetString()!;
                if (name is not ("formatVersion" or "storedFiles" or "storageReplicas" or "storedFileCount" or "storageReplicaCount")
                    || !schema.Fields.Add(name)) throw Invalid();
                schema.Pending = name;
                continue;
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0) throw Invalid();
            schema.Ended = true;
        }
    }

    private static InvalidDataException Invalid() => new("Некорректная структура или запись перечня документов резервной копии.");
    private sealed record ParseResult(int Consumed, JsonReaderState State, DatabaseBackupInventoryRow? Row);
    private sealed class Schema
    {
        public bool Started, Ended;
        public string? Array, Pending;
        public readonly HashSet<string> Fields = new(StringComparer.Ordinal);
        public readonly HashSet<string> RecordFields = new(StringComparer.Ordinal);
        public long Files, Replicas;
        public long? ExpectedFiles, ExpectedReplicas;
        public void Validate()
        {
            if (!Ended || Fields.Count != 5 || Files != ExpectedFiles || Replicas != ExpectedReplicas) throw Invalid();
        }
    }
}
