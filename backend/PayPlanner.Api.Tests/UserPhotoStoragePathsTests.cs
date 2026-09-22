using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class UserPhotoStoragePathsTests
{
    [Fact]
    public void ResolveRoot_DefaultsToPersistentApplicationFileStorage()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), $"payplanner-photo-paths-{Guid.NewGuid():N}");
        var environment = new TestEnvironment(contentRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FileStorage:RootPath"] = Path.Combine(contentRoot, "shared-files")
            })
            .Build();

        var root = UserPhotoStoragePaths.ResolveRoot(environment, configuration);

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "shared-files", "user-photos")), root);
    }

    [Theory]
    [InlineData("/user-photos/42/avatar.png", "42", "avatar.png")]
    [InlineData("https://crm.example.test/user-photos/42/avatar.png", "42", "avatar.png")]
    public void ResolvePhotoPath_MapsOnlySafeUserPhotoUrls(string url, string userDirectory, string fileName)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"payplanner-photo-root-{Guid.NewGuid():N}"));

        var path = UserPhotoStoragePaths.ResolvePhotoPath(root, url);

        Assert.Equal(Path.Combine(root, userDirectory, fileName), path);
    }

    [Theory]
    [InlineData("/user-photos/../secret.txt")]
    [InlineData("/other/avatar.png")]
    public void ResolvePhotoPath_RejectsTraversalAndForeignPaths(string url)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"payplanner-photo-root-{Guid.NewGuid():N}"));

        Assert.Null(UserPhotoStoragePaths.ResolvePhotoPath(root, url));
    }

    private sealed class TestEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(contentRoot, "wwwroot");
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
