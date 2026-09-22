using System.Security.Cryptography;

namespace PayPlanner.Api.Services.Storage;

public interface ILegacyLocalStorageLocator
{
    bool TryCreateLocator(string path, out string locator);
}

public sealed class LegacyLocalStorageProvider : IStorageProvider, ILegacyLocalStorageLocator
{
    private readonly IReadOnlyDictionary<string, string> _roots;

    public LegacyLocalStorageProvider(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["files"] = ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
            ["profile-current"] = UserPhotoStoragePaths.ResolveRoot(environment, configuration),
            ["profile-legacy"] = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "user-photos"),
            ["esign-current"] = string.IsNullOrWhiteSpace(configuration["ElectronicSignature:StorageRoot"])
                ? Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration), "electronic-signatures")
                : ResolveConfigured(environment.ContentRootPath, configuration["ElectronicSignature:StorageRoot"]!),
            ["esign-legacy"] = Path.Combine(environment.ContentRootPath, "storage", "electronic-signatures"),
        };
        _roots = candidates
            .Select(item => new KeyValuePair<string, string>(item.Key, Path.GetFullPath(item.Value)))
            .GroupBy(item => item.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(group => group.First())
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
    }

    public string Key => "legacy-local";
    public StorageProviderCapabilities Capabilities { get; } = new(true, true, false, false, false, false, false, false, false);

    public bool TryCreateLocator(string path, out string locator)
    {
        locator = string.Empty;
        if (string.IsNullOrWhiteSpace(path)) return false;
        var fullPath = Path.GetFullPath(path);
        foreach (var root in _roots.OrderByDescending(item => item.Value.Length))
        {
            if (!IsWithin(fullPath, root.Value)) continue;
            var relative = Path.GetRelativePath(root.Value, fullPath).Replace('\\', '/');
            if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal)) continue;
            locator = $"{root.Key}/{relative}";
            return true;
        }
        return false;
    }

    public Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new StorageWriteResult(StorageOperationStatus.Conflict, request.Locator, 0, null,
            Error: "Legacy storage is read-only; migrate through the orchestration service."));

    public async Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken cancellationToken)
    {
        var path = Resolve(locator);
        if (!File.Exists(path)) return new(StorageOperationStatus.NotFound, locator, 0, null, null);
        try
        {
            var info = new FileInfo(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return new(StorageOperationStatus.Succeeded, locator, info.Length,
                Convert.ToHexString(hash).ToLowerInvariant(), info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new(StorageOperationStatus.Unavailable, locator, 0, null, null, Error: ex.Message);
        }
    }

    public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(locator);
        if (!File.Exists(path)) return Task.FromResult(new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0));
        try
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
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
        _ = Resolve(locator);
        return Task.FromResult(new StorageDeleteResult(StorageOperationStatus.Conflict, locator,
            "Physical deletion of legacy files is disabled until migration acceptance."));
    }

    private string Resolve(string locator)
    {
        var normalized = (locator ?? string.Empty).Trim().Replace('\\', '/').Trim('/');
        var separator = normalized.IndexOf('/');
        if (separator <= 0 || separator == normalized.Length - 1)
            throw new ArgumentException("Legacy locator must use 'root/path' format.", nameof(locator));
        var rootKey = normalized[..separator];
        if (!_roots.TryGetValue(rootKey, out var root))
            throw new ArgumentException("Legacy locator root is unknown.", nameof(locator));
        var relative = normalized[(separator + 1)..];
        if (relative.Split('/').Any(part => part is "." or ".."))
            throw new ArgumentException("Legacy locator contains relative path segments.", nameof(locator));
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(path, root) || HasReparsePoint(root, path))
            throw new ArgumentException("Legacy locator escapes the configured root.", nameof(locator));
        return path;
    }

    private static bool HasReparsePoint(string root, string path)
    {
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current) && IsWithin(current, root))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(root), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(fullPath, fullRoot, comparison) || fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static string ResolveConfigured(string contentRoot, string configured)
        => Path.IsPathRooted(configured) ? configured : Path.Combine(contentRoot, configured);
}
