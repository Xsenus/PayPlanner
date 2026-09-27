namespace PayPlanner.Api.Services;

public interface IBackupDirectoryReader
{
    IReadOnlyList<string> GetFiles(string directory, string pattern);
    IReadOnlyList<string> GetDirectories(string directory);
    bool IsEmpty(string directory);
}

public sealed class PhysicalBackupDirectoryReader : IBackupDirectoryReader
{
    public IReadOnlyList<string> GetFiles(string directory, string pattern)
        => Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);

    public IReadOnlyList<string> GetDirectories(string directory)
        => Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);

    public bool IsEmpty(string directory)
        => !Directory.EnumerateFileSystemEntries(directory).Any();
}
