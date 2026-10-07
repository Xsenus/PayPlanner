namespace PayPlanner.Api.Services.Storage;

// Only the native call gets an extended path. Persisted/product paths and
// caller validation remain unchanged; this grants no filesystem authority.
internal static class WindowsStoragePath
{
    internal static string ForNativeCall(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal)) return fullPath;
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }
}
