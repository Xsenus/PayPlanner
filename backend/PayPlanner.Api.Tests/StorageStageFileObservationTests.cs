using Microsoft.Win32.SafeHandles;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageStageFileObservationTests
{
    [Theory]
    [InlineData(FileAccess.Write)]
    [InlineData(FileAccess.ReadWrite)]
    public async Task OpenDescriptorAgreesWithCapacityGroupAndPreservesBytesAndPosition(FileAccess access)
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_stage_observation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "owned.tmp");
            var original = new byte[] { 1, 2, 3, 4 };
            await using (var file = new FileStream(path, FileMode.CreateNew, access, FileShare.None))
            {
                await file.WriteAsync(original);
                await file.FlushAsync();
                var position = file.Position;
                var observed = StorageStageFileObservation.Observe(file.SafeFileHandle);
                Assert.NotNull(observed);
                Assert.Equal(StorageStageFileIdentity.Observe(file.SafeFileHandle), observed.FileIdentity);
                Assert.True(string.Equals(Path.GetFullPath(path), observed.AbsolutePath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                var capacity = LocalStorageCapacityProbe.Observe(root, "owned-test-resource", 0);
                Assert.False(string.IsNullOrWhiteSpace(capacity.GroupId));
                Assert.Equal(capacity.GroupId, observed.GroupId);
                Assert.Equal(position, file.Position);
                Assert.Equal(original.Length, file.Length);
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RetainedDescriptorReportsRenamedObjectInsteadOfReplacementAtOriginalPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pp_stage_observation_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "owned.tmp");
            var moved = Path.Combine(root, "moved.tmp");
            var original = new byte[] { 1, 2, 3, 4 };
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete);
            await file.WriteAsync(original);
            await file.FlushAsync();
            var before = StorageStageFileObservation.Observe(file.SafeFileHandle);
            Assert.NotNull(before);
            File.Move(path, moved);
            await File.WriteAllBytesAsync(path, original); // same bytes, different object
            var after = StorageStageFileObservation.Observe(file.SafeFileHandle);
            Assert.NotNull(after);
            Assert.Equal(before.FileIdentity, after.FileIdentity);
            Assert.Equal(before.GroupId, after.GroupId);
            Assert.True(string.Equals(Path.GetFullPath(moved), after.AbsolutePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            Assert.NotEqual(before.AbsolutePath, after.AbsolutePath);
            await using var foreign = File.OpenRead(path);
            var replacement = StorageStageFileObservation.Observe(foreign.SafeFileHandle);
            Assert.NotNull(replacement);
            Assert.NotEqual(after.FileIdentity, replacement.FileIdentity);
            Assert.Equal(original, await File.ReadAllBytesAsync(path));
            file.Position = 0;
            var retainedBytes = new byte[original.Length];
            await file.ReadExactlyAsync(retainedBytes);
            Assert.Equal(original, retainedBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ClosedInvalidAndMissingDescriptorsRemainUnknown()
    {
        Assert.Null(StorageStageFileObservation.Observe(null));
        using var invalid = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        Assert.Null(StorageStageFileObservation.Observe(invalid));
        var closed = new SafeFileHandle(new IntPtr(123), ownsHandle: false);
        closed.Dispose();
        Assert.Null(StorageStageFileObservation.Observe(closed));
    }
}
