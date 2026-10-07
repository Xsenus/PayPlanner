using System.Runtime.CompilerServices;

namespace PayPlanner.Api.Tests;

internal static class TestRepository
{
    public static DirectoryInfo FindDirectory([CallerFilePath] string sourceFile = "")
        => FindFromDirectories([Path.GetDirectoryName(sourceFile), Directory.GetCurrentDirectory(), AppContext.BaseDirectory]);

    internal static DirectoryInfo FindFromDirectories(IEnumerable<string?> starts)
    {
        // A relocated testhost can live outside the checkout. Keep the actual
        // source location first, then invocation and artifact ancestry fallbacks.
        foreach (var start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "vite.config.ts"))
                    && File.Exists(Path.Combine(directory.FullName, "backend", "PayPlanner.Api.csproj")))
                    return directory;
        }
        throw new InvalidOperationException("Project root is missing.");
    }
}
