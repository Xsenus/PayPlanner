namespace PayPlanner.Api.Services.Storage;

/// <summary>Publishes a new legacy upload without ever replacing another file.</summary>
public static class ExclusiveUploadFileWriter
{
    public static async Task WriteNewAsync(string path, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var created = false;
        try
        {
            await using (var destination = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                created = true;
                await write(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }
        }
        catch
        {
            if (created) File.Delete(path);
            throw;
        }
    }
}
