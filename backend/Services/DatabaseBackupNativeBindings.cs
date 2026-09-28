using System.Buffers;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupNativeBinding(string Kind, DatabaseBackupStoredFileReference Reference,
    string? ArchivePartRelativePath, string? ArchiveEntryPath, bool ExternalReference, long? ActualSizeBytes, string? ActualSha256);

/// <summary>A bounded, portable mapping. Physical source paths are never used while verifying restored bytes.</summary>
public static class DatabaseBackupNativeBindings
{
    // The reference reader already bounds a source record to 64 KiB. Leave room for
    // its wrapper, portable archive path and sealed checksum without reducing that limit.
    private const int LineLimit = 131072;
    private static readonly byte[] Newline = [(byte)'\n'];
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 8,
    };
    private sealed record Header(string Kind, int FormatVersion, string InventorySha256, string State);
    private sealed record Footer(string Kind, long BindingCount);

    public static async Task WritePlanAsync(Stream inventory, Stream destination,
        DatabaseBackupFileBindingResolver resolver, string archivePartRelativePath, string inventorySha256, CancellationToken cancellationToken)
    {
        _ = DatabaseBackupArchiveHash.ToComment(inventorySha256);
        await WriteLineAsync(destination, new Header("header", 1, inventorySha256, "planned"), cancellationToken);
        long count = 0;
        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken))
        {
            if (row.StoredFile is not { } reference) continue;
            var resolved = resolver.Resolve(reference);
            var binding = new DatabaseBackupNativeBinding("binding", reference,
                resolved.ExternalReference ? null : archivePartRelativePath, resolved.ArchiveEntryPath,
                resolved.ExternalReference, null, null);
            ValidateBinding(binding, sealedMapping: false);
            await WriteLineAsync(destination, binding, cancellationToken);
            count = checked(count + 1);
        }
        await WriteLineAsync(destination, new Footer("footer", count), cancellationToken);
    }

    public static async Task SealAsync(Stream plan, Stream destination, ZipArchive? archive,
        string inventorySha256, CancellationToken cancellationToken)
    {
        await WriteLineAsync(destination, new Header("header", 1, inventorySha256, "complete"), cancellationToken);
        long count = 0;
        await foreach (var binding in ReadAsync(plan, inventorySha256, sealedMapping: false, cancellationToken))
        {
            var completed = binding;
            if (!binding.ExternalReference)
            {
                var entry = archive?.GetEntry(binding.ArchiveEntryPath!);
                if (entry is null || string.IsNullOrEmpty(entry.Name))
                    throw new InvalidDataException($"Документ снимка отсутствует в файловом архиве: {binding.Reference.SourceKind} #{binding.Reference.SourceId}.");
                var hash = DatabaseBackupArchiveHash.FromComment(entry.Comment);
                CheckExpected(binding.Reference, new(entry.Length, hash));
                completed = binding with { ActualSizeBytes = entry.Length, ActualSha256 = hash };
            }
            ValidateBinding(completed, sealedMapping: true);
            await WriteLineAsync(destination, completed, cancellationToken);
            count = checked(count + 1);
        }
        await WriteLineAsync(destination, new Footer("footer", count), cancellationToken);
    }

    public static async Task VerifyAsync(Stream inventory, Stream bindings, string inventorySha256,
        Func<string, string, CancellationToken, Task<DatabaseBackupArchiveFileHash?>> getArchivedFile,
        CancellationToken cancellationToken)
    {
        await using var mapping = ReadAsync(bindings, inventorySha256, sealedMapping: true, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(inventory, cancellationToken))
        {
            if (row.StoredFile is not { } reference) continue;
            if (!await mapping.MoveNextAsync() || mapping.Current.Reference != reference)
                throw new InvalidDataException("Сопоставление архивных файлов не соответствует перечню снимка базы.");
            var binding = mapping.Current;
            if (binding.ExternalReference) continue;
            var actual = await getArchivedFile(binding.ArchivePartRelativePath!, binding.ArchiveEntryPath!, cancellationToken);
            if (actual is null || actual.SizeBytes != binding.ActualSizeBytes
                || !string.Equals(actual.Sha256, binding.ActualSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Архивный или восстановленный документ не соответствует сохранённому сопоставлению.");
            CheckExpected(reference, actual);
        }
        if (await mapping.MoveNextAsync())
            throw new InvalidDataException("Сопоставление содержит лишние архивные документы.");
    }

    public static async IAsyncEnumerable<DatabaseBackupNativeBinding> ReadAsync(Stream source,
        string inventorySha256, bool sealedMapping, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var headerSeen = false;
        var footerSeen = false;
        long count = 0;
        await foreach (var line in ReadLinesAsync(source, cancellationToken))
        {
            var parsed = ParseLine(line);
            if (footerSeen) throw Invalid();
            if (!headerSeen)
            {
                var header = parsed as Header ?? throw Invalid();
                if (header.Kind != "header" || header.FormatVersion != 1
                    || header.State != (sealedMapping ? "complete" : "planned")
                    || !string.Equals(header.InventorySha256, inventorySha256, StringComparison.OrdinalIgnoreCase)) throw Invalid();
                _ = DatabaseBackupArchiveHash.ToComment(header.InventorySha256);
                headerSeen = true;
                continue;
            }
            if (parsed is Footer footer)
            {
                if (footer.BindingCount != count) throw Invalid();
                footerSeen = true;
                continue;
            }
            var binding = parsed as DatabaseBackupNativeBinding ?? throw Invalid();
            ValidateBinding(binding, sealedMapping);
            count = checked(count + 1);
            yield return binding;
        }
        if (!headerSeen || !footerSeen) throw Invalid();
    }

    private static void ValidateBinding(DatabaseBackupNativeBinding binding, bool sealedMapping)
    {
        var reference = binding.Reference;
        if (binding.Kind != "binding" || reference is null || reference.SourceId < 1
            || reference.SourceKind is not ("task-file" or "case-document" or "chat-attachment" or "messenger-delivery"
                or "esign-original" or "esign-artifact" or "profile-photo")
            || string.IsNullOrWhiteSpace(reference.StoredPath) || reference.SizeBytes is < 0
            || reference.Version is < 1 || reference.ParentId is < 1) throw Invalid();
        if (reference.Sha256 is not null) _ = DatabaseBackupArchiveHash.ToComment(reference.Sha256);
        if (binding.ExternalReference)
        {
            if (reference.SourceKind != "profile-photo" || !Uri.TryCreate(reference.StoredPath, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || uri.AbsolutePath.StartsWith(UserPhotoStoragePaths.RequestPath + "/", StringComparison.OrdinalIgnoreCase)
                || binding.ArchivePartRelativePath is not null || binding.ArchiveEntryPath is not null
                || binding.ActualSizeBytes is not null || binding.ActualSha256 is not null) throw Invalid();
            return;
        }
        ValidatePortablePath(binding.ArchivePartRelativePath);
        ValidatePortablePath(binding.ArchiveEntryPath);
        if (reference.SourceKind == "chat-attachment" && reference.ParentId is null) throw Invalid();
        if (!sealedMapping)
        {
            if (binding.ActualSizeBytes is not null || binding.ActualSha256 is not null) throw Invalid();
            return;
        }
        if (binding.ActualSizeBytes is null or < 0 || binding.ActualSha256 is null) throw Invalid();
        _ = DatabaseBackupArchiveHash.ToComment(binding.ActualSha256);
        CheckExpected(reference, new(binding.ActualSizeBytes.Value, binding.ActualSha256));
    }

    private static void ValidatePortablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Contains('\\')
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.Contains(':') || segment.Contains('\0')))
            throw Invalid();
    }

    private static void CheckExpected(DatabaseBackupStoredFileReference reference, DatabaseBackupArchiveFileHash actual)
    {
        if (reference.SizeBytes is { } size && size != actual.SizeBytes
            || reference.Sha256 is { } expected && !string.Equals(expected, actual.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Архивный документ не соответствует размеру или SHA-256 снимка базы.");
    }

    private static async Task WriteLineAsync<T>(Stream output, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length >= LineLimit) throw new InvalidDataException("Запись сопоставления архивных файлов превышает допустимый размер.");
        await output.WriteAsync(bytes, cancellationToken);
        await output.WriteAsync(Newline, cancellationToken);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadLinesAsync(Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(LineLimit);
        var count = 0;
        var start = 0;
        var scanned = 0;
        var final = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var newline = buffer.AsSpan(scanned, count - scanned).IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    newline += scanned;
                    if (newline == start) throw Invalid();
                    yield return buffer.AsMemory(start, newline - start);
                    start = newline + 1;
                    scanned = start;
                    continue;
                }
                scanned = count;
                if (final)
                {
                    if (start != count) throw Invalid(); // Every record, including the footer, ends in LF.
                    yield break;
                }
                // Compact only an incomplete tail before the next read, not after every
                // record. Each incoming byte is scanned once, even with one-byte reads.
                if (start != 0)
                {
                    count -= start;
                    if (count != 0) Buffer.BlockCopy(buffer, start, buffer, 0, count);
                    scanned -= start;
                    start = 0;
                }
                if (count == LineLimit) throw new InvalidDataException("Запись сопоставления архивных файлов превышает допустимый размер.");
                var read = await source.ReadAsync(buffer.AsMemory(count, LineLimit - count), cancellationToken);
                count += read;
                final = read == 0;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 8 });
        var objects = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw Invalid();
        }
    }

    private static object ParseLine(ReadOnlyMemory<byte> line)
    {
        try
        {
            RejectDuplicateProperties(line.Span);
            using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("kind", out var kind)) throw Invalid();
            return kind.GetString() switch
            {
                "header" => JsonSerializer.Deserialize<Header>(line.Span, Options) ?? throw Invalid(),
                "footer" => JsonSerializer.Deserialize<Footer>(line.Span, Options) ?? throw Invalid(),
                "binding" => JsonSerializer.Deserialize<DatabaseBackupNativeBinding>(line.Span, Options) ?? throw Invalid(),
                _ => throw Invalid(),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        { throw new InvalidDataException("Некорректная запись сопоставления архивных файлов.", error); }
    }

    private static InvalidDataException Invalid() => new("Некорректное или неполное сопоставление архивных файлов.");
}
