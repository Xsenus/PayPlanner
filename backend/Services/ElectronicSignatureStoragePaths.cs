namespace PayPlanner.Api.Services;

public static class ElectronicSignatureStoragePaths
{
    public const string StorageRootConfigurationKey = "ElectronicSignature:StorageRoot";

    public static string ResolveRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredRoot = configuration[StorageRootConfigurationKey];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration), "electronic-signatures")
            : Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(environment.ContentRootPath, configuredRoot));
    }

    public static string ResolveLegacyRoot(IWebHostEnvironment environment)
        => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "storage", "electronic-signatures"));
}
