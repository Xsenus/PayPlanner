using System.Security.Cryptography;

namespace PayPlanner.Api.Services.Storage;

internal static class ConnectedStorageLocator
{
    public static (string ConnectionId, string Path) Parse(string locator)
    {
        var normalized = (locator ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            throw new ArgumentException("Connected storage locator must use 'connection-id/path' format.", nameof(locator));
        }

        var connectionId = normalized[..separator];
        var path = normalized[(separator + 1)..];
        if (connectionId is "." or ".." || path.Split('/').Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Connected storage locator contains relative path segments.", nameof(locator));
        }
        return (connectionId, path);
    }
}

public sealed class S3StorageProvider(IS3StorageService service) : IStorageProvider
{
    public string Key => "s3";
    public StorageProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true,
        SupportsRangeReads: false,
        SupportsConditionalCreate: false,
        SupportsAtomicPromote: false,
        SupportsMultipartUpload: false,
        SupportsPresignedUrls: false,
        SupportsServerSideCopy: true,
        SupportsTrash: false,
        SupportsVersioning: false);

    public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(request.Locator);
        try
        {
            await using var hashing = new HashingReadStream(request.Content);
            await service.UploadAsync(connectionId, path, hashing, request.ContentType, request.Overwrite, cancellationToken);
            var hash = hashing.GetSha256();
            return ValidateWrite(request, hashing.BytesRead, hash);
        }
        catch (S3StorageOperationException ex)
        {
            return new StorageWriteResult(Map(ex.StatusCode), request.Locator, 0, null, Error: ex.Message);
        }
    }

    public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(locator);
        try
        {
            await using var download = await service.OpenReadAsync(connectionId, path, cancellationToken);
            var hash = await SHA256.HashDataAsync(download.Content, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator, download.SizeBytes,
                Convert.ToHexString(hash).ToLowerInvariant(), null);
        }
        catch (S3StorageOperationException ex)
        {
            return new(Map(ex.StatusCode), locator, 0, null, null, Error: ex.Message);
        }
    }

    public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(locator);
        try
        {
            var download = await service.OpenReadAsync(connectionId, path, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator, download.Content, download.SizeBytes);
        }
        catch (S3StorageOperationException ex)
        {
            return new(Map(ex.StatusCode), locator, null, 0, ex.Message);
        }
    }

    public async Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(locator);
        try
        {
            await service.DeleteObjectAsync(connectionId, path, recursive: false, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator);
        }
        catch (S3StorageOperationException ex)
        {
            return new(Map(ex.StatusCode), locator, ex.Message);
        }
    }

    private static StorageWriteResult ValidateWrite(StorageWriteRequest request, long size, string hash)
    {
        if (request.ExpectedSizeBytes is { } expectedSize && expectedSize != size)
            return new(StorageOperationStatus.Conflict, request.Locator, size, hash, Error: "Object size does not match the expected value.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
            && !string.Equals(request.ExpectedSha256, hash, StringComparison.OrdinalIgnoreCase))
            return new(StorageOperationStatus.Conflict, request.Locator, size, hash, Error: "Object checksum does not match the expected value.");
        return new(StorageOperationStatus.Succeeded, request.Locator, size, hash);
    }

    private static StorageOperationStatus Map(int statusCode) => statusCode switch
    {
        404 => StorageOperationStatus.NotFound,
        409 => StorageOperationStatus.Conflict,
        408 or 425 or 429 or >= 500 => StorageOperationStatus.Unavailable,
        _ => StorageOperationStatus.Conflict,
    };
}

public sealed class YandexDiskStorageProvider(IYandexDiskService service) : IStorageProvider
{
    public string Key => "yandex-disk";
    public StorageProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true,
        SupportsRangeReads: false,
        SupportsConditionalCreate: false,
        SupportsAtomicPromote: false,
        SupportsMultipartUpload: false,
        SupportsPresignedUrls: false,
        SupportsServerSideCopy: true,
        SupportsTrash: true,
        SupportsVersioning: false);

    public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(request.Locator);
        try
        {
            await using var hashing = new HashingReadStream(request.Content);
            await service.UploadAsync(connectionId, path, hashing, request.ContentType, request.Overwrite, cancellationToken);
            var hash = hashing.GetSha256();
            if (request.ExpectedSizeBytes is { } expectedSize && expectedSize != hashing.BytesRead)
                return new(StorageOperationStatus.Conflict, request.Locator, hashing.BytesRead, hash, Error: "Object size does not match the expected value.");
            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
                && !string.Equals(request.ExpectedSha256, hash, StringComparison.OrdinalIgnoreCase))
                return new(StorageOperationStatus.Conflict, request.Locator, hashing.BytesRead, hash, Error: "Object checksum does not match the expected value.");
            return new(StorageOperationStatus.Succeeded, request.Locator, hashing.BytesRead, hash);
        }
        catch (YandexDiskOperationException ex)
        {
            return new(Map(ex.StatusCode), request.Locator, 0, null, Error: ex.Message);
        }
    }

    public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
    {
        var read = await OpenReadAsync(locator, cancellationToken);
        await using (read)
        {
            if (read.Status != StorageOperationStatus.Succeeded || read.Content is null)
                return new(read.Status, locator, 0, null, null, Error: read.Error);
            var hash = await SHA256.HashDataAsync(read.Content, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator, read.SizeBytes,
                Convert.ToHexString(hash).ToLowerInvariant(), null);
        }
    }

    public async Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(locator);
        try
        {
            var download = await service.OpenReadAsync(connectionId, path, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator, download.Content, download.SizeBytes);
        }
        catch (YandexDiskOperationException ex)
        {
            return new(Map(ex.StatusCode), locator, null, 0, ex.Message);
        }
    }

    public async Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
    {
        var (connectionId, path) = ConnectedStorageLocator.Parse(locator);
        try
        {
            await service.DeleteAsync(connectionId, path, permanently: true, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator);
        }
        catch (YandexDiskOperationException ex)
        {
            return new(Map(ex.StatusCode), locator, ex.Message);
        }
    }

    private static StorageOperationStatus Map(int statusCode) => statusCode switch
    {
        404 => StorageOperationStatus.NotFound,
        409 => StorageOperationStatus.Conflict,
        408 or 425 or 429 or >= 500 => StorageOperationStatus.Unavailable,
        _ => StorageOperationStatus.Conflict,
    };
}

internal sealed class HashingReadStream(Stream inner) : Stream, IAsyncDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finalized;
    private string? _sha256;
    public long BytesRead { get; private set; }

    public string GetSha256()
    {
        if (!_finalized)
        {
            _sha256 = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
            _finalized = true;
            _hash.Dispose();
        }
        return _sha256!;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
        {
            _hash.AppendData(buffer.Span[..read]);
            BytesRead += read;
        }
        return read;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _hash.AppendData(buffer, offset, read);
            BytesRead += read;
        }
        return read;
    }

    // StreamContent used by native integrations may dispose this wrapper before
    // the adapter reads the final checksum. The caller owns the underlying stream;
    // the hash is finalized and disposed by GetSha256 instead.
    protected override void Dispose(bool disposing) => base.Dispose(disposing);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public sealed class OwnedProviderStream(Stream inner, IAsyncDisposable owner) : Stream
{
    private bool _disposed;
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            owner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await owner.DisposeAsync();
        }
        GC.SuppressFinalize(this);
    }
}
