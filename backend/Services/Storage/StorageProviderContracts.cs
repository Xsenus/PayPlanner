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

public interface IStorageProvider
{
    string Key { get; }
    StorageProviderCapabilities Capabilities { get; }
    Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken);
    Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken);
    Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken);
    Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken cancellationToken);
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
