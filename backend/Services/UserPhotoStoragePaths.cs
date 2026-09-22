namespace PayPlanner.Api.Services;

public static class UserPhotoStoragePaths
{
    public const string StorageRootConfigurationKey = "ProfilePhotos:StorageRoot";
    public const string RequestPath = "/user-photos";

    public static string ResolveRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredRoot = configuration[StorageRootConfigurationKey]?.Trim();
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration), "user-photos")
            : Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(environment.ContentRootPath, configuredRoot);
        return Path.GetFullPath(root);
    }

    public static string ResolveLegacyRoot(IWebHostEnvironment environment)
    {
        var webRoot = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        return Path.GetFullPath(Path.Combine(webRoot, "user-photos"));
    }

    public static string? ResolvePhotoPath(string root, string? photoUrl)
    {
        if (string.IsNullOrWhiteSpace(photoUrl))
        {
            return null;
        }

        var photoPath = photoUrl.Trim();
        if (Uri.TryCreate(photoPath, UriKind.Absolute, out var absoluteUri))
        {
            photoPath = absoluteUri.AbsolutePath;
        }

        if (!photoPath.StartsWith($"{RequestPath}/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relativePath = photoPath[(RequestPath.Length + 1)..]
            .Replace('/', Path.DirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)
            ? fullPath
            : null;
    }
}
