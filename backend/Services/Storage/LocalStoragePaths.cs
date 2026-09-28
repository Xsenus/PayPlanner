namespace PayPlanner.Api.Services.Storage;

public static class LocalStoragePaths
{
    public const string RootConfigurationKey = "StorageOrchestration:LocalRoot";

    public static string ResolveRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredRoot = configuration[RootConfigurationKey]?.Trim();
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(ApplicationFileStoragePaths.ResolveRoot(environment, configuration), "objects")
            : Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(environment.ContentRootPath, configuredRoot));
    }
}
