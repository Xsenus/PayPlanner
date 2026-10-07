using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class ExclusiveUploadFileWriterTests
{
    [Fact]
    public async Task ExistingFileIsNeverOpenedOrDeleted()
    {
        var path = OwnedPath();
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var called = false;
            await Assert.ThrowsAsync<IOException>(() => ExclusiveUploadFileWriter.WriteNewAsync(path,
                (_, _) => { called = true; return Task.CompletedTask; }, CancellationToken.None));
            Assert.False(called);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrCancelledCopyRemovesOnlyItsPartialFile(bool cancelled)
    {
        var path = OwnedPath();
        try
        {
            async Task WriteAsync(Stream destination, CancellationToken token)
            {
                await destination.WriteAsync(new byte[] { 4, 5 }, token);
                if (cancelled) throw new OperationCanceledException(token);
                throw new IOException("Owned synthetic copy failure.");
            }
            if (cancelled)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExclusiveUploadFileWriter.WriteNewAsync(
                    path, WriteAsync, CancellationToken.None));
            else
                await Assert.ThrowsAsync<IOException>(() => ExclusiveUploadFileWriter.WriteNewAsync(
                    path, WriteAsync, CancellationToken.None));
            Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CompletedFileContainsExactBytes()
    {
        var path = OwnedPath();
        try
        {
            await ExclusiveUploadFileWriter.WriteNewAsync(path,
                (destination, token) => destination.WriteAsync(new byte[] { 6, 7, 8 }, token).AsTask(),
                CancellationToken.None);
            Assert.Equal(new byte[] { 6, 7, 8 }, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static string OwnedPath() => Path.Combine(Path.GetTempPath(),
        "pp_exclusive_upload_" + Guid.NewGuid().ToString("N") + ".bin");
}
