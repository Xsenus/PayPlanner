using System.Diagnostics;

namespace PayPlanner.Api.Services;

public static class BackupDirectoryLease
{
    private const string LockFileName = ".database-backup.lock";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public static async Task<FileStream> AcquireAsync(
        string backupRootDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRootDirectory);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        Directory.CreateDirectory(backupRootDirectory);
        var lockPath = Path.Combine(backupRootDirectory, LockFileName);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (stopwatch.Elapsed < timeout)
            {
                await Task.Delay(RetryDelay, cancellationToken);
            }
            catch (IOException ex)
            {
                throw new IOException("Не удалось получить блокировку каталога резервных копий до истечения времени ожидания.", ex);
            }
        }
    }
}
