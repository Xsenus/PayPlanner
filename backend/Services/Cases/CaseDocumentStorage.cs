using Microsoft.AspNetCore.Hosting;

namespace PayPlanner.Api.Services;

public interface ICaseDocumentStorage
{
    string BuildRelativePath(int caseId, string storedFileName);
    string ResolvePath(string storagePath);
    Task SaveAsync(string storagePath, Stream content, CancellationToken cancellationToken);
    Task SaveAsync(string storagePath, byte[] content, CancellationToken cancellationToken);
    bool Exists(string storagePath);
    void DeleteIfExists(string storagePath);
}

public static class ApplicationFileStoragePaths
{
    public const string RootPathConfigurationKey = "FileStorage:RootPath";

    public static string ResolveRoot(
        IWebHostEnvironment? environment = null,
        IConfiguration? configuration = null,
        string? defaultContentRoot = null)
    {
        var contentRoot = environment?.ContentRootPath
            ?? defaultContentRoot
            ?? Directory.GetCurrentDirectory();
        var configuredRoot = configuration?[RootPathConfigurationKey]?.Trim();
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(contentRoot, "App_Data")
            : Path.IsPathRooted(configuredRoot)
                ? configuredRoot
                : Path.Combine(contentRoot, configuredRoot);

        return Path.GetFullPath(root);
    }
}

public class LocalCaseDocumentStorage : ICaseDocumentStorage
{
    private readonly string _root;

    public LocalCaseDocumentStorage(
        IWebHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        _root = Path.Combine(
            ApplicationFileStoragePaths.ResolveRoot(environment, configuration),
            "case-documents");
    }

    public string BuildRelativePath(int caseId, string storedFileName)
        => Path.Combine(caseId.ToString(), storedFileName);

    public string ResolvePath(string storagePath)
    {
        var root = Path.GetFullPath(_root);
        var fullPath = Path.GetFullPath(Path.Combine(root, storagePath));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Некорректный путь файла дела.");
        }

        return fullPath;
    }

    public async Task SaveAsync(string storagePath, Stream content, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        var created = false;
        try
        {
            await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            await content.CopyToAsync(stream, cancellationToken);
        }
        catch
        {
            // A failed CreateNew does not give this operation ownership of an existing file.
            if (created) DeleteIfExists(storagePath);
            throw;
        }
    }

    public async Task SaveAsync(string storagePath, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content, writable: false);
        await SaveAsync(storagePath, stream, cancellationToken);
    }

    public bool Exists(string storagePath)
        => File.Exists(ResolvePath(storagePath));

    public void DeleteIfExists(string storagePath)
    {
        try
        {
            var fullPath = ResolvePath(storagePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Файл уже удален или временно занят. Запись в БД важнее физической уборки.
        }
    }
}
