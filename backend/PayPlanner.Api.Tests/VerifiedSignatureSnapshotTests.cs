using System.Reflection;
using System.Security.Cryptography;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class VerifiedSignatureSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMoveKeepsOriginalIdentityReadonlyBytesAndFileAfterDisposal(bool longPath)
    {
        using var root = new SnapshotRoot(longPath);
        var shortCapacity = LocalStorageCapacityProbe.Observe(root.OwnedBase, "short", 0);
        var snapshotCapacity = LocalStorageCapacityProbe.Observe(root.Root, "snapshot", 0);
        Assert.NotNull(snapshotCapacity.AvailableBytes);
        Assert.Equal(shortCapacity.GroupId, snapshotCapacity.GroupId);
        var name = Guid.NewGuid().ToString("N");
        await using var snapshot = Assert.IsType<SnapshotLease>(await MoveAsync(root, name, root.RootIdentity, root.FileIdentity));
        Assert.Equal(Path.Combine(root.Root, name, "original.pdf"), snapshot.Path);
        Assert.False(File.Exists(root.Source));
        Assert.True(snapshot.Content.CanRead);
        Assert.False(snapshot.Content.CanWrite);
        Assert.Equal(root.FileIdentity, StorageStageFileIdentity.Observe(snapshot.Content.SafeFileHandle));
        Assert.Throws<NotSupportedException>(() => snapshot.Content.WriteByte(1));
        using var copy = new MemoryStream();
        await snapshot.Content.CopyToAsync(copy);
        Assert.Equal(root.Bytes, copy.ToArray());
        Assert.True(await snapshot.VerifyAsync());
        Assert.Equal(0, snapshot.Content.Position);
        await snapshot.DisposeAsync();
        await snapshot.DisposeAsync();
        Assert.False(await snapshot.VerifyAsync());
        Assert.Equal(root.Bytes, await File.ReadAllBytesAsync(snapshot.Path));
        using var reopened = File.OpenRead(snapshot.Path);
        Assert.Equal(root.FileIdentity, StorageStageFileIdentity.Observe(reopened.SafeFileHandle));
        if (longPath) Assert.True(snapshot.Path.Length > 300);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingDestinationDirectoryIsNeverReusedOrOverwritten(bool containsForeignFile)
    {
        using var root = new SnapshotRoot();
        var name = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(root.Root, name);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "original.pdf");
        var foreign = "%PDF-1.7 foreign destination"u8.ToArray();
        if (containsForeignFile) await File.WriteAllBytesAsync(path, foreign);
        var directoryIdentity = StorageStageRootIdentity.Observe(directory);
        Assert.Null(await MoveAsync(root, name, root.RootIdentity, root.FileIdentity));
        Assert.Equal(directoryIdentity, StorageStageRootIdentity.Observe(directory));
        Assert.Equal(root.Bytes, await File.ReadAllBytesAsync(root.Source));
        if (containsForeignFile) Assert.Equal(foreign, await File.ReadAllBytesAsync(path));
        else Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOriginalProofDoesNotMoveOrDeleteAnything(bool unknownRoot)
    {
        using var root = new SnapshotRoot();
        Assert.Null(await MoveAsync(root, Guid.NewGuid().ToString("N"),
            unknownRoot ? null : root.RootIdentity, unknownRoot ? root.FileIdentity : null));
        Assert.Equal(root.Bytes, await File.ReadAllBytesAsync(root.Source));
        Assert.Empty(Directory.EnumerateDirectories(root.Root));
    }

    [Fact]
    public async Task CancellationBeforeNativeMovePreservesSourceAndDoesNotCreateDestination()
    {
        using var root = new SnapshotRoot();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var error = await Record.ExceptionAsync(() => MoveAsync(root, Guid.NewGuid().ToString("N"),
            root.RootIdentity, root.FileIdentity, cancellation.Token));
        Assert.Equal(cancellation.Token, Assert.IsAssignableFrom<OperationCanceledException>(error).CancellationToken);
        Assert.Equal(root.Bytes, await File.ReadAllBytesAsync(root.Source));
        Assert.Empty(Directory.EnumerateDirectories(root.Root));
    }

    [Fact]
    public async Task LiveWindowsSnapshotBlocksWritesAndFileOrDirectoryReplacementUntilClosed()
    {
        Assert.True(OperatingSystem.IsWindows(), "This gate qualifies Windows sharing; Linux requires its own stand.");
        using var root = new SnapshotRoot();
        await using var snapshot = Assert.IsType<SnapshotLease>(await MoveAsync(root, Guid.NewGuid().ToString("N"), root.RootIdentity, root.FileIdentity));
        Assert.Throws<IOException>(() =>
        {
            using var writer = File.OpenHandle(snapshot.Path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        });
        Assert.Throws<IOException>(() => File.Move(snapshot.Path, snapshot.Path + ".replacement"));
        var directory = Path.GetDirectoryName(snapshot.Path)!;
        Assert.Throws<IOException>(() => Directory.Move(directory, directory + ".replacement"));
        Assert.Throws<IOException>(() => Directory.Move(root.Root, root.Root + ".replacement"));
        Assert.True(await snapshot.VerifyAsync());
        await snapshot.DisposeAsync();
        Assert.Equal(root.Bytes, await File.ReadAllBytesAsync(snapshot.Path));
        var moved = root.Root + ".closed";
        Assert.StartsWith(root.OwnedBase + Path.DirectorySeparatorChar, Path.GetFullPath(moved));
        Directory.Move(root.Root, moved);
        Directory.Move(moved, root.Root);
        await File.AppendAllTextAsync(snapshot.Path, "Permitted only after ownership released");
    }

    private static readonly Type SnapshotType = typeof(ElectronicSignatureService).Assembly.GetType("PayPlanner.Api.Services.Storage.VerifiedSignatureSnapshot", throwOnError: true)!;

    private static async Task<SnapshotLease?> MoveAsync(SnapshotRoot root, string name, string? rootIdentity,
        string? fileIdentity, CancellationToken ct = default)
    {
        var task = (Task)SnapshotType.GetMethod("MoveAsync", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [root.Source, root.Root, rootIdentity, fileIdentity, root.Bytes.LongLength, root.Sha256, name, ct])!;
        await task;
        var value = task.GetType().GetProperty("Result")!.GetValue(task);
        return value is null ? null : new SnapshotLease(value);
    }

    private sealed class SnapshotLease(object value) : IAsyncDisposable
    {
        internal FileStream Content => (FileStream)SnapshotType.GetProperty("Content", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
        internal string Path => (string)SnapshotType.GetProperty("FullPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(value)!;
        internal Task<bool> VerifyAsync() => (Task<bool>)SnapshotType.GetMethod("VerifyAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(value, [CancellationToken.None])!;
        public ValueTask DisposeAsync() => ((IAsyncDisposable)value).DisposeAsync();
    }

    private sealed class SnapshotRoot : IDisposable
    {
        internal string OwnedBase { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pp_signature_snapshot_" + Guid.NewGuid().ToString("N")));
        internal string Root { get; }
        internal string Source => Path.Combine(Root, "owned-source.tmp");
        internal byte[] Bytes { get; } = "%PDF-1.7 native snapshot boundary fixture"u8.ToArray();
        internal string Sha256 => Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();
        internal string RootIdentity { get; }
        internal string FileIdentity { get; }
        internal SnapshotRoot(bool longPath = false)
        {
            Root = Path.Combine(OwnedBase, "root");
            while (longPath && Root.Length < 310) Root = Path.Combine(Root, "owned-nested-long-path-segment");
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Source, Bytes);
            RootIdentity = Assert.IsType<string>(StorageStageRootIdentity.Observe(Root));
            using var file = File.OpenRead(Source);
            FileIdentity = Assert.IsType<string>(StorageStageFileIdentity.Observe(file.SafeFileHandle));
        }
        public void Dispose()
        {
            Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar, OwnedBase);
            Assert.Matches("^pp_signature_snapshot_[0-9a-f]{32}$", Path.GetFileName(OwnedBase));
            Directory.Delete(OwnedBase, recursive: true);
        }
    }
}
