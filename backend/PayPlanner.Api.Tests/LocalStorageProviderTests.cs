using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LocalStorageProviderTests
{
    [Fact]
    public async Task PutHeadReadDelete_RoundTripsBytesAndChecksum()
    {
        await using var fixture = new Fixture();
        var bytes = "storage-provider-content"u8.ToArray();
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

        var put = await fixture.Provider.PutAsync(
            new StorageWriteRequest("tenant/objects/file.bin", new MemoryStream(bytes), "application/octet-stream", bytes.Length, expectedHash),
            CancellationToken.None);
        var head = await fixture.Provider.HeadAsync("tenant/objects/file.bin", CancellationToken.None);
        var read = await fixture.Provider.OpenReadAsync("tenant/objects/file.bin", CancellationToken.None);
        using var copied = new MemoryStream();
        await read.Content!.CopyToAsync(copied);
        await read.DisposeAsync();
        var deleted = await fixture.Provider.DeleteAsync("tenant/objects/file.bin", CancellationToken.None);
        var missing = await fixture.Provider.HeadAsync("tenant/objects/file.bin", CancellationToken.None);

        Assert.Equal(StorageOperationStatus.Succeeded, put.Status);
        Assert.Equal(expectedHash, put.Sha256);
        Assert.Equal(StorageOperationStatus.Succeeded, head.Status);
        Assert.Equal(expectedHash, head.Sha256);
        Assert.Equal(bytes, copied.ToArray());
        Assert.Equal(StorageOperationStatus.Succeeded, deleted.Status);
        Assert.Equal(StorageOperationStatus.NotFound, missing.Status);
    }

    [Fact]
    public async Task Put_IsConditionalByDefaultAndValidatesChecksum()
    {
        await using var fixture = new Fixture();
        var first = await fixture.Provider.PutAsync(
            new StorageWriteRequest("object.bin", new MemoryStream("one"u8.ToArray()), "application/octet-stream"),
            CancellationToken.None);
        var conflict = await fixture.Provider.PutAsync(
            new StorageWriteRequest("object.bin", new MemoryStream("two"u8.ToArray()), "application/octet-stream"),
            CancellationToken.None);
        var checksumMismatch = await fixture.Provider.PutAsync(
            new StorageWriteRequest("other.bin", new MemoryStream("bytes"u8.ToArray()), "application/octet-stream", ExpectedSha256: "wrong"),
            CancellationToken.None);

        Assert.Equal(StorageOperationStatus.Succeeded, first.Status);
        Assert.Equal(StorageOperationStatus.Conflict, conflict.Status);
        Assert.Equal(StorageOperationStatus.Conflict, checksumMismatch.Status);
        Assert.Equal(StorageOperationStatus.NotFound, (await fixture.Provider.HeadAsync("other.bin", CancellationToken.None)).Status);
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData(3L, false)]
    [InlineData(0L, true)]
    [InlineData(3L, true)]
    public async Task Put_StopsOversizedNonSeekingSourceBeforeAnotherReadAndPreservesOriginal(long expected, bool overwrite)
    {
        await using var fixture = new Fixture();
        if (overwrite)
            Assert.Equal(StorageOperationStatus.Succeeded, (await fixture.Provider.PutAsync(
                new("bounded.bin", new MemoryStream([9, 8]), "application/octet-stream"), CancellationToken.None)).Status);
        using var source = new OneChunkThenFailStream();
        var result = await fixture.Provider.PutAsync(new("bounded.bin", source, "application/octet-stream", expected,
            Overwrite: overwrite), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Conflict, result.Status);
        Assert.Equal(1, source.ReadCalls); Assert.Equal(0, result.SizeBytes); Assert.Null(result.Sha256);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
        await using var stored = await fixture.Provider.OpenReadAsync("bounded.bin", CancellationToken.None);
        if (overwrite)
        {
            Assert.Equal(StorageOperationStatus.Succeeded, stored.Status);
            using var copy = new MemoryStream(); await stored.Content!.CopyToAsync(copy);
            Assert.Equal(new byte[] { 9, 8 }, copy.ToArray());
        }
        else Assert.Equal(StorageOperationStatus.NotFound, stored.Status);
    }

    [Fact]
    public async Task Put_RejectsNegativeSizeBeforeReadingOrCreatingFiles()
    {
        await using var fixture = new Fixture(); using var source = new OneChunkThenFailStream();
        var result = await fixture.Provider.PutAsync(new("nested/negative.bin", source, "application/octet-stream", -1), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Conflict, result.Status); Assert.Equal(0, source.ReadCalls);
        Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    [Fact]
    public async Task Put_DirectoryFailureReturnsUnavailableWithoutReadingOrReplacingAnything()
    {
        await using var fixture = new Fixture();
        var blocker = Path.Combine(fixture.Root, "objects"); await File.WriteAllBytesAsync(blocker, [7, 6]);
        using var source = new OneChunkThenFailStream();
        var result = await fixture.Provider.PutAsync(new("nested/fail.bin", source, "application/octet-stream", 4), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Unavailable, result.Status); Assert.Equal(0, source.ReadCalls);
        Assert.Equal(new byte[] { 7, 6 }, await File.ReadAllBytesAsync(blocker));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Put_PreallocationDoesNotTurnShortSourceIntoSuccessAndAllowsEmptyFile()
    {
        await using var fixture = new Fixture();
        var shortFile = await fixture.Provider.PutAsync(new("short.bin", new MemoryStream([1, 2]), "application/octet-stream", 4), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Conflict, shortFile.Status);
        Assert.Equal(StorageOperationStatus.NotFound, (await fixture.Provider.HeadAsync("short.bin", CancellationToken.None)).Status);
        var empty = await fixture.Provider.PutAsync(new("empty.bin", new MemoryStream(), "application/octet-stream", 0), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Succeeded, empty.Status); Assert.Equal(0, empty.SizeBytes);
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Put_PreCancelledRequestDoesNotReadOrCreateDirectories()
    {
        await using var fixture = new Fixture(); using var source = new OneChunkThenFailStream();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Provider.PutAsync(
            new("nested/cancel.bin", source, "application/octet-stream", 4), cancellation.Token));
        Assert.Equal(0, source.ReadCalls); Assert.Empty(Directory.GetFileSystemEntries(fixture.Root));
    }

    private sealed class OneChunkThenFailStream : Stream
    {
        public int ReadCalls { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++ReadCalls > 1) throw new InvalidOperationException("Oversized source must not be read again.");
            new byte[] { 1, 2, 3, 4 }.CopyTo(buffer); return ValueTask.FromResult(4);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("folder/../../secret")]
    [InlineData("")]
    public async Task Operations_RejectUnsafeLocator(string locator)
    {
        await using var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.PutAsync(
            new StorageWriteRequest(locator, new MemoryStream([1]), "application/octet-stream"),
            CancellationToken.None));
    }

    [Fact]
    public async Task OperationsRejectDirectoryLinkOutsideConfiguredRoot()
    {
        await using var fixture = new Fixture();
        var outside = Path.Combine(fixture.Root, "outside");
        var storageRoot = Path.Combine(fixture.Root, "objects");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(storageRoot);
        var payload = Path.Combine(outside, "payload.bin");
        await File.WriteAllBytesAsync(payload, [7, 8, 9]);
        var alias = Path.Combine(storageRoot, "alias");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(alias);
            start.ArgumentList.Add(outside);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start owned junction fixture.");
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(alias, outside);

        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.HeadAsync("alias/payload.bin", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.OpenReadAsync("alias/payload.bin", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.DeleteAsync("alias/payload.bin", CancellationToken.None));
            await Assert.ThrowsAsync<ArgumentException>(() => fixture.Provider.PutAsync(
                new StorageWriteRequest("alias/new.bin", new MemoryStream([1]), "application/octet-stream"), CancellationToken.None));
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(payload));
            Assert.False(File.Exists(Path.Combine(outside, "new.bin")));
        }
        finally
        {
            if (Directory.Exists(alias)) Directory.Delete(alias);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"payplanner-local-provider-{Guid.NewGuid():N}");
        public string Root => _root;
        public LocalStorageProvider Provider { get; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StorageOrchestration:LocalRoot"] = Path.Combine(_root, "objects")
                })
                .Build();
            Provider = new LocalStorageProvider(new TestEnvironment(_root), configuration);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
