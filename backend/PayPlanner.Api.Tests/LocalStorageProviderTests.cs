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

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"payplanner-local-provider-{Guid.NewGuid():N}");
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
