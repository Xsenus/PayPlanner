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

public class LocalCaseDocumentStorage : ICaseDocumentStorage
{
    private readonly string _root;

    public LocalCaseDocumentStorage(IWebHostEnvironment? environment = null)
    {
        _root = Path.Combine(
            environment?.ContentRootPath ?? Directory.GetCurrentDirectory(),
            "App_Data",
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

        await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await content.CopyToAsync(stream, cancellationToken);
    }

    public async Task SaveAsync(string storagePath, byte[] content, CancellationToken cancellationToken)
    {
        var fullPath = ResolvePath(storagePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(content, cancellationToken);
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
