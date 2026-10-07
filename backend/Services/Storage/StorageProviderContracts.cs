namespace PayPlanner.Api.Services.Storage;

public enum StorageOperationStatus
{
    Succeeded,
    NotFound,
    Conflict,
    Unavailable,
    Unknown,
}

public sealed record StorageProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsRangeReads,
    bool SupportsConditionalCreate,
    bool SupportsAtomicPromote,
    bool SupportsMultipartUpload,
    bool SupportsPresignedUrls,
    bool SupportsServerSideCopy,
    bool SupportsTrash,
    bool SupportsVersioning);

public sealed record StorageWriteRequest(
    string Locator,
    Stream Content,
    string ContentType,
    long? ExpectedSizeBytes = null,
    string? ExpectedSha256 = null,
    bool Overwrite = false);

public sealed record StorageWriteResult(
    StorageOperationStatus Status,
    string Locator,
    long SizeBytes,
    string? Sha256,
    string? ProviderVersion = null,
    string? Error = null);

public sealed record StorageObjectMetadata(
    StorageOperationStatus Status,
    string Locator,
    long SizeBytes,
    string? Sha256,
    DateTime? LastModifiedUtc,
    string? ProviderVersion = null,
    string? Error = null);

public sealed record StorageReadResult(
    StorageOperationStatus Status,
    string Locator,
    Stream? Content,
    long SizeBytes,
    string? Error = null) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
        => Content?.DisposeAsync() ?? ValueTask.CompletedTask;
}

public sealed record StorageDeleteResult(
    StorageOperationStatus Status,
    string Locator,
    string? Error = null);

// Describes where destination bytes consume capacity. Unknown adapters cannot
// certify a native-volume budget merely because their key looks like a cloud.
public enum StorageCapacityDomain { Unknown, Native, Remote }

public interface IStorageProvider
{
    string Key { get; }
    StorageCapacityDomain CapacityDomain => StorageCapacityDomain.Unknown;
    StorageProviderCapabilities Capabilities { get; }
    Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken);
    Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken);
    Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken);
    Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken);
}

// Providers own locator syntax. Recovery uses a durable, isolated destination instead
// of overwriting damaged bytes; its identity must be stable across queue retries.
public interface IStorageRepairDestinationProvider
{
    string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey);
}

// Connected adapters route by the locator, not by the queue's ConnectionId.
// Reject mismatched catalog metadata before any provider I/O.
public interface IStorageConnectedLocatorProvider
{
    bool TryGetConnectionId(string locator, out string connectionId);
}

internal static class StorageRepairDestination
{
    public static string Path(Guid generationId, string idempotencyKey)
        => $"recovery/{generationId:N}/{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant()}.bin";
}

public interface IStorageProviderRegistry
{
    IReadOnlyCollection<IStorageProvider> Providers { get; }
    bool TryGet(string key, out IStorageProvider provider);
    IStorageProvider GetRequired(string key);
}

public sealed class StorageProviderRegistry(IEnumerable<IStorageProvider> providers) : IStorageProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IStorageProvider> _providers = providers
        .GroupBy(provider => provider.Key, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IStorageProvider> Providers => _providers.Values.ToArray();

    public bool TryGet(string key, out IStorageProvider provider)
        => _providers.TryGetValue(key, out provider!);

    public IStorageProvider GetRequired(string key)
        => TryGet(key, out var provider)
            ? provider
            : throw new InvalidOperationException($"Storage provider '{key}' is not registered.");
}
