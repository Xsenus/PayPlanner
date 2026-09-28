using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class LegacyLocalStorageProviderAliasTests
{
    [Theory]
    [InlineData("files", false)]
    [InlineData("profile-current", false)]
    [InlineData("profile-legacy", false)]
    [InlineData("esign-current", true)]
    [InlineData("esign-legacy", true)]
    public async Task CoincidentPhysicalRootsPreserveEveryPersistedLocator(string rootKey, bool signature)
    {
        var root = Path.Combine(Path.GetTempPath(), $"payplanner-legacy-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var photos = Path.Combine(root, "wwwroot", "user-photos");
            var signatures = Path.Combine(root, "storage", "electronic-signatures");
            Directory.CreateDirectory(photos);
            Directory.CreateDirectory(signatures);
            var bytes = "сохранённый документ"u8.ToArray();
            var source = Path.Combine(signature ? signatures : photos, "document.bin");
            await File.WriteAllBytesAsync(source, bytes);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = photos,
                ["ProfilePhotos:StorageRoot"] = photos,
                ["ElectronicSignature:StorageRoot"] = signatures
            }).Build();
            var provider = new LegacyLocalStorageProvider(new TestEnvironment(root), configuration);
            var locator = $"{rootKey}/document.bin";
            var metadata = await provider.HeadAsync(locator, CancellationToken.None);
            await using var read = await provider.OpenReadAsync(locator, CancellationToken.None);
            using var destination = new MemoryStream();
            Assert.Equal(StorageOperationStatus.Succeeded, metadata.Status);
            Assert.Equal(StorageOperationStatus.Succeeded, read.Status);
            await read.Content!.CopyToAsync(destination);
            Assert.Equal(bytes, destination.ToArray());
            Assert.Equal(bytes.Length, metadata.SizeBytes);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(), metadata.Sha256);
            await Assert.ThrowsAsync<ArgumentException>(() => provider.HeadAsync($"{rootKey}/../outside.bin", CancellationToken.None));
            var deletion = await provider.DeleteAsync(locator, CancellationToken.None);
            Assert.Equal(StorageOperationStatus.Conflict, deletion.Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(root, recursive: true); }
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
