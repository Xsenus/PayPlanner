using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class TestRepositoryTests
{
    [Fact]
    public void CompiledSourceFindsTheCheckoutOutsideTheArtifactTree()
    {
        var repository = TestRepository.FindDirectory();
        Assert.True(File.Exists(Path.Combine(repository.FullName, "vite.config.ts")));
        Assert.True(File.Exists(Path.Combine(repository.FullName, "backend", "PayPlanner.Api.csproj")));
        Assert.Equal(Path.GetFullPath(Path.Combine(repository.FullName, "backend", "PayPlanner.Api.Tests")),
            Path.GetFullPath(SourceDirectory()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FindsSourceInvocationOrArtifactAncestorInOrder(int candidate)
    {
        using var fixture = new Fixture();
        var starts = Enumerable.Range(0, 3).Select(i => Path.Combine(fixture.Root, i.ToString(), "nested")).ToArray();
        var expected = Path.Combine(fixture.Root, candidate.ToString());
        fixture.MakeRepository(expected);
        Assert.Equal(expected, TestRepository.FindFromDirectories(starts).FullName);
    }

    [Fact]
    public void FirstMatchingCheckoutWins()
    {
        using var fixture = new Fixture();
        var source = Path.Combine(fixture.Root, "source");
        var other = Path.Combine(fixture.Root, "other");
        fixture.MakeRepository(source);
        fixture.MakeRepository(other);
        Assert.Equal(source, TestRepository.FindFromDirectories([source, other]).FullName);
    }

    [Theory]
    [InlineData("vite.config.ts")]
    [InlineData("backend/PayPlanner.Api.csproj")]
    public void PartialCheckoutDoesNotReplaceTheRequiredRepository(string marker)
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Root, marker);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Empty);
        Assert.Throws<InvalidOperationException>(() => TestRepository.FindFromDirectories([null, "", fixture.Root]));
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
        => Path.GetDirectoryName(sourceFile)!;

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"pp_test_repository_{Guid.NewGuid():N}");
        public Fixture() => Directory.CreateDirectory(Root);
        public void MakeRepository(string path)
        {
            Directory.CreateDirectory(Path.Combine(path, "backend"));
            File.WriteAllText(Path.Combine(path, "vite.config.ts"), string.Empty);
            File.WriteAllText(Path.Combine(path, "backend", "PayPlanner.Api.csproj"), string.Empty);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
