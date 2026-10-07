using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageRootIdentityTests
{
    [Fact]
    public void NativeRootIdentityIsStableAndDistinguishesDirectoryObjects()
    {
        using var f = new OwnedRootIdentityFixture();
        var first = Directory.CreateDirectory(Path.Combine(f.Root, "first")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(f.Root, "second")).FullName;
        var identity = StorageStageRootIdentity.Observe(first);
        Assert.NotNull(identity);
        Assert.InRange(identity.Length, 1, 160);
        Assert.Equal(identity, StorageStageRootIdentity.Observe(first));
        Assert.NotNull(StorageStageRootIdentity.Observe(second));
        Assert.NotEqual(identity, StorageStageRootIdentity.Observe(second));
    }

    [Fact]
    public void NativeRootIdentitySurvivesMoveAndDetectsReplacementAtSamePath()
    {
        using var f = new OwnedRootIdentityFixture();
        var path = Directory.CreateDirectory(Path.Combine(f.Root, "original")).FullName;
        var retained = Path.Combine(f.Root, "retained");
        File.WriteAllBytes(Path.Combine(path, "owned.bin"), [1, 2, 3]);
        var identity = StorageStageRootIdentity.Observe(path);
        Assert.NotNull(identity);
        Directory.Move(path, retained);
        Directory.CreateDirectory(path);
        Assert.Equal(identity, StorageStageRootIdentity.Observe(retained));
        var replacement = StorageStageRootIdentity.Observe(path);
        Assert.NotNull(replacement);
        Assert.NotEqual(identity, replacement);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(retained, "owned.bin")));
        Assert.False(File.Exists(Path.Combine(path, "owned.bin")));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("file")]
    [InlineData("relative")]
    public void NativeRootIdentityRetainsUnknownForUnsupportedAuthority(string kind)
    {
        using var f = new OwnedRootIdentityFixture();
        var path = Path.Combine(f.Root, "candidate");
        if (kind == "file") File.WriteAllBytes(path, [9]);
        if (kind == "relative") path = "owned-relative-root";
        Assert.Null(StorageStageRootIdentity.Observe(path));
    }

    [Fact]
    public void NativeDirectoryObservationNeverAcceptsRegularOrClosedFileHandle()
    {
        using var f = new OwnedRootIdentityFixture();
        using var file = new FileStream(Path.Combine(f.Root, "owned.bin"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        Assert.NotNull(StorageStageFileIdentity.Observe(file.SafeFileHandle));
        Assert.Null(StorageStageRootIdentity.ObserveDescriptor(file.SafeFileHandle));
        var handle = file.SafeFileHandle;
        file.Dispose();
        Assert.Null(StorageStageRootIdentity.ObserveDescriptor(handle));
    }

    private sealed class OwnedRootIdentityFixture : IDisposable
    {
        private readonly string _name = "pp_root_identity_" + Guid.NewGuid().ToString("N");
        internal string Root { get; }
        internal OwnedRootIdentityFixture()
        {
            Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), _name));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(Root));
            Assert.Equal(_name, Path.GetFileName(Root));
            Assert.True(Guid.TryParseExact(_name["pp_root_identity_".Length..], "N", out _));
            Directory.Delete(Root, recursive: true);
        }
    }
}
