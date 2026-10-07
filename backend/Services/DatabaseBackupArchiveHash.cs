using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace PayPlanner.Api.Services;

public sealed record DatabaseBackupArchiveFileHash(long SizeBytes, string Sha256);

/// <summary>Hashes the exact bytes written, with a fixed-size, cleared pooled buffer.</summary>
public static class DatabaseBackupArchiveHash
{
    public const string Format = "sha256-v1";
    private const string CommentPrefix = "payplanner-sha256-v1:";

    public static string ToComment(string hash)
    {
        ValidateHash(hash);
        return CommentPrefix + hash.ToLowerInvariant();
    }

    public static string FromComment(string comment)
    {
        if (string.IsNullOrEmpty(comment) || !comment.StartsWith(CommentPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("В архивном файле отсутствует обязательный SHA-256.");
        var hash = comment[CommentPrefix.Length..];
        ValidateHash(hash);
        return hash.ToLowerInvariant();
    }

    private static void ValidateHash(string hash)
    {
        if (hash is null || hash.Length != 64 || hash.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException("Некорректный SHA-256 архивного файла.");
    }

    public static async Task ValidateEntriesAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var expected = FromComment(entry.Comment);
            await using var source = entry.Open();
            var actual = await CopyAsync(source, Stream.Null, cancellationToken);
            if (actual.SizeBytes != entry.Length || !string.Equals(actual.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Размер или SHA-256 архивного документа не совпадает с его содержимым.");
        }
    }

    public static async Task<DatabaseBackupArchiveFileHash> CopyAsync(
        Stream source, Stream destination, CancellationToken cancellationToken, long? maximumSizeBytes = null)
    {
        if (maximumSizeBytes is < 0) throw new ArgumentOutOfRangeException(nameof(maximumSizeBytes));
        cancellationToken.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long size = 0;
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer.AsMemory(0, 81920), cancellationToken);
                if (count == 0) break;
                if (maximumSizeBytes is { } maximum && count > maximum - size)
                    throw new InvalidDataException("Документ превышает размер, сохранённый в снимке базы.");
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                hash.AppendData(buffer, 0, count);
                size = checked(size + count);
            }
            return new(size, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>Checks that a seekable file's preflight bytes are exactly those archived.</summary>
    public static async Task<DatabaseBackupArchiveFileHash> CopyStableAsync(
        Stream source, Stream destination, CancellationToken cancellationToken)
    {
        if (!source.CanRead || !source.CanSeek || source.Position != 0)
            throw new ArgumentException("Stable archive input must be readable, seekable and positioned at its start.", nameof(source));
        var length = source.Length;
        var expected = await CopyAsync(source, Stream.Null, cancellationToken, length);
        if (expected.SizeBytes != length || source.Length != length)
            throw new InvalidDataException("Размер документа изменился во время проверки резервной копии.");
        source.Position = 0;
        var actual = await CopyAsync(source, destination, cancellationToken, length);
        if (actual != expected || source.Length != length)
            throw new InvalidDataException("Документ изменился во время упаковки резервной копии.");
        return actual;
    }
}
