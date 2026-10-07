using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class PackageMetadataTests
{
    private const string ExpectedPackageName = "payplanner";
    private const string StarterPackageName = "vite-react-typescript-starter";

    [Fact]
    public void PackageJson_UsesProductPackageName()
    {
        var root = FindRepositoryRoot();
        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));

        var packageName = packageJson.RootElement.GetProperty("name").GetString();

        Assert.Equal(ExpectedPackageName, packageName);
        Assert.NotEqual(StarterPackageName, packageName);
    }

    [Fact]
    public void PackageLock_RootPackageNameMatchesPackageJson()
    {
        var root = FindRepositoryRoot();
        using var packageJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package.json")));
        using var packageLock = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "package-lock.json")));

        var packageName = packageJson.RootElement.GetProperty("name").GetString();
        var lockName = packageLock.RootElement.GetProperty("name").GetString();
        var rootLockName = packageLock.RootElement
            .GetProperty("packages")
            .GetProperty(string.Empty)
            .GetProperty("name")
            .GetString();

        Assert.Equal(ExpectedPackageName, packageName);
        Assert.Equal(packageName, lockName);
        Assert.Equal(packageName, rootLockName);
    }

    [Fact]
    public void RepositoryMetadata_DoesNotReferenceStarterPackageName()
    {
        var root = FindRepositoryRoot();
        var metadataFiles = new[]
        {
            Path.Combine(root, "package.json"),
            Path.Combine(root, "package-lock.json"),
        };

        foreach (var file in metadataFiles)
        {
            var content = File.ReadAllText(file);
            Assert.DoesNotContain(StarterPackageName, content, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = TestRepository.FindDirectory();

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "package.json"))
                && File.Exists(Path.Combine(current.FullName, "package-lock.json"))
                && Directory.Exists(Path.Combine(current.FullName, "backend")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
