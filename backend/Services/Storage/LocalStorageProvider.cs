using System.Security.Cryptography;

namespace PayPlanner.Api.Services.Storage;

public sealed class LocalStorageProvider : IStorageProvider, IStorageRepairDestinationProvider, IStorageCapacityProvider
{
    private readonly string _root;

    public LocalStorageProvider(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _root = LocalStoragePaths.ResolveRoot(environment, configuration);
    }

    public string Key => "local";
    public StorageCapacityDomain CapacityDomain => StorageCapacityDomain.Native;

    public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
        long minimumFreeBytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(connectionId is null ? LocalStorageCapacityProbe.Observe(_root, resourceKey, minimumFreeBytes)
            : new StorageCapacityResource(resourceKey, "", null, minimumFreeBytes, DateTimeOffset.UtcNow));
    }

    public string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey)
    {
        _ = ResolvePath(originalLocator);
        return StorageRepairDestination.Path(generationId, idempotencyKey);
    }

    public StorageProviderCapabilities Capabilities { get; } = new(
        SupportsStreaming: true,
        SupportsRangeReads: true,
        SupportsConditionalCreate: true,
        SupportsAtomicPromote: true,
        SupportsMultipartUpload: false,
        SupportsPresignedUrls: false,
        SupportsServerSideCopy: false,
        SupportsTrash: false,
        SupportsVersioning: false);

    public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ExpectedSizeBytes is < 0)
            return new(StorageOperationStatus.Conflict, request.Locator, 0, null, Error: "Expected size must be non-negative.");
        var destinationPath = ResolvePath(request.Locator);
        if (!request.Overwrite && File.Exists(destinationPath))
        {
            return new StorageWriteResult(StorageOperationStatus.Conflict, request.Locator, 0, null, Error: "Object already exists.");
        }

        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            RejectLinkedPath(destinationPath);
            long size = 0;
            string sha256;
            // Ask supported filesystems for space before reading the source. This
            // is not a quota/capacity certificate: unsupported platforms may ignore it.
            await using (var output = new FileStream(temporaryPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 81920,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                PreallocationSize = request.ExpectedSizeBytes.GetValueOrDefault()
            }))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await request.Content.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    if (request.ExpectedSizeBytes.HasValue && read > request.ExpectedSizeBytes.Value - size)
                        return new(StorageOperationStatus.Conflict, request.Locator, size, null,
                            Error: "Object exceeds the expected size.");
                    size += read;
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }

                await output.FlushAsync(cancellationToken);
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (request.ExpectedSizeBytes.HasValue && request.ExpectedSizeBytes.Value != size)
            {
                return new StorageWriteResult(StorageOperationStatus.Conflict, request.Locator, size, sha256, Error: "Object size does not match the expected value.");
            }

            if (!string.IsNullOrWhiteSpace(request.ExpectedSha256)
                && !string.Equals(request.ExpectedSha256, sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new StorageWriteResult(StorageOperationStatus.Conflict, request.Locator, size, sha256, Error: "Object checksum does not match the expected value.");
            }

            try
            {
                RejectLinkedPath(destinationPath);
                File.Move(temporaryPath, destinationPath, request.Overwrite);
            }
            catch (IOException) when (!request.Overwrite && File.Exists(destinationPath))
            {
                return new StorageWriteResult(StorageOperationStatus.Conflict, request.Locator, size, sha256, Error: "Object already exists.");
            }

            return new StorageWriteResult(StorageOperationStatus.Succeeded, request.Locator, size, sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StorageWriteResult(StorageOperationStatus.Unavailable, request.Locator, 0, null, Error: ex.Message);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(locator);
        if (!File.Exists(fullPath))
        {
            return new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null);
        }

        try
        {
            var info = new FileInfo(fullPath);
            await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return new StorageObjectMetadata(
                StorageOperationStatus.Succeeded,
                locator,
                info.Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new StorageObjectMetadata(StorageOperationStatus.Unavailable, locator, 0, null, null, Error: ex.Message);
        }
    }

    public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolvePath(locator);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0));
        }

        try
        {
            var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, locator, stream, stream.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new StorageReadResult(StorageOperationStatus.Unavailable, locator, null, 0, ex.Message));
        }
    }

    public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = ResolvePath(locator);
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(new StorageDeleteResult(StorageOperationStatus.NotFound, locator));
        }

        try
        {
            File.Delete(fullPath);
            return Task.FromResult(new StorageDeleteResult(StorageOperationStatus.Succeeded, locator));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new StorageDeleteResult(StorageOperationStatus.Unavailable, locator, ex.Message));
        }
    }

    private string ResolvePath(string locator)
    {
        var normalized = (locator ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Storage locator is required.", nameof(locator));
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Any(part => part is "." or ".."))
        {
            throw new ArgumentException("Storage locator must not contain relative path segments.", nameof(locator));
        }

        var fullPath = Path.GetFullPath(Path.Combine(_root, Path.Combine(parts)));
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("Storage locator is outside the configured root.", nameof(locator));
        }

        RejectLinkedPath(fullPath);

        return fullPath;
    }

    private void RejectLinkedPath(string fullPath)
    {
        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var current = fullPath; !string.Equals(current, root, comparison); current = Path.GetDirectoryName(current)!)
        {
            if (current is null || current.Length < root.Length) break;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Storage locator crosses a linked path.", nameof(fullPath));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
