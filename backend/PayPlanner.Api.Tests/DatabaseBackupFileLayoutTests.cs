using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupFileLayoutTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "payplanner-backup-layout-" + Guid.NewGuid().ToString("N"));

    private string DirectoryPath(string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void NestedAndEqualRootsKeepEveryLogicalMappingWithoutDuplicateArchives()
    {
        var files = DirectoryPath("files");
        var photos = DirectoryPath("files/photos");
        var signatures = DirectoryPath("signatures");
        var layout = DatabaseBackupFileLayout.Create([
            new("file-storage", files), new("profile-photos", photos),
            new("local-objects", files), new("electronic-signatures", signatures)], Path.Combine(root, "backups"));
        Assert.Equal(2, layout.ArchiveRoots.Count);
        Assert.Equal("file-storage/photos/41/image.png", layout.GetArchiveEntryPath(Path.Combine(photos, "41/image.png")));
        Assert.Equal("electronic-signatures/42/document.pdf", layout.GetArchiveEntryPath(Path.Combine(signatures, "42/document.pdf")));
        Assert.Equal("file-storage/photos", Assert.Single(layout.RootMappings, item => item.Name == "profile-photos").ArchivePrefix);
        Assert.Equal("file-storage", Assert.Single(layout.RootMappings, item => item.Name == "local-objects").ArchivePrefix);
    }

    [Fact]
    public void ExcludedSiblingAndTraversalPathsCannotBecomeArchiveEntries()
    {
        var files = DirectoryPath("files");
        var backups = DirectoryPath("files/backups");
        var layout = DatabaseBackupFileLayout.Create([new("file-storage", files)], backups);
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(backups, "dump.sqlite")));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(root, "files-other/file.txt")));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(files, "../outside.txt")));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(files));
        Assert.Throws<ArgumentException>(() => layout.GetArchiveEntryPath("relative.txt"));
        if (!OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(files, "folder\\..\\outside.txt")));
    }

    [Fact]
    public void MissingOptionalRootIsNotFalselyMappedAndRequiredRootFails()
    {
        var absent = Path.Combine(root, "absent");
        var excluded = Path.Combine(root, "backups");
        var layout = DatabaseBackupFileLayout.Create([new("file-storage", absent)], excluded);
        Assert.Empty(layout.ArchiveRoots);
        Assert.Empty(layout.RootMappings);
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(absent, "file.txt")));
        Assert.Throws<DirectoryNotFoundException>(() => DatabaseBackupFileLayout.Create([new("file-storage", absent, true)], excluded));
    }

    [Fact]
    public void KnownRootLookupDoesNotAllowAnotherIncludedDirectory()
    {
        var files = DirectoryPath("files");
        var photos = DirectoryPath("photos");
        var layout = DatabaseBackupFileLayout.Create([new("file-storage", files), new("profile-photos", photos)],
            Path.Combine(root, "backups"));
        Assert.Equal("profile-photos/image.png", layout.GetArchiveEntryPath(Path.Combine(photos, "image.png"), "profile-photos"));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(photos, "image.png"), "file-storage"));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(files, "file.txt"), "unknown"));
    }

    [Fact]
    public void InvalidOrDuplicatePrefixAndRelativeRootAreRejected()
    {
        var files = DirectoryPath("files");
        var excluded = Path.Combine(root, "backups");
        Assert.Throws<ArgumentException>(() => DatabaseBackupFileLayout.Create([new("../escape", files)], excluded));
        Assert.Throws<ArgumentException>(() => DatabaseBackupFileLayout.Create([new("files", files), new("files", files)], excluded));
        Assert.Throws<ArgumentException>(() => DatabaseBackupFileLayout.Create([new("files", files), new("FILES", files)], excluded));
        Assert.Throws<ArgumentException>(() => DatabaseBackupFileLayout.Create([new("files", "relative")], excluded));
    }

    [Fact]
    public void VolumeRootAndExcludedDirectoriesAreRejectedBeforeEnumeration()
    {
        var backups = DirectoryPath("backups");
        var nested = DirectoryPath("backups/nested");
        Assert.Throws<IOException>(() => DatabaseBackupFileLayout.Create([
            new("files", Path.GetPathRoot(root)!, true)], backups));
        Assert.Throws<IOException>(() => DatabaseBackupFileLayout.Create([new("files", backups)], backups));
        Assert.Throws<IOException>(() => DatabaseBackupFileLayout.Create([new("files", nested)], backups));
        // A backup directory nested inside an ordinary source remains supported and is excluded per file.
        var layout = DatabaseBackupFileLayout.Create([new("files", root)], backups);
        Assert.Equal("files/document.txt", layout.GetArchiveEntryPath(Path.Combine(root, "document.txt")));
        Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(nested, "dump.sqlite")));
    }

    [Fact]
    public void FileInsteadOfDirectoryAndUnboundedRootListsFail()
    {
        var files = DirectoryPath("files");
        var wrongRoot = Path.Combine(files, "not-a-directory");
        File.WriteAllText(wrongRoot, "fixture");
        var excluded = Path.Combine(root, "backups");
        Assert.Throws<IOException>(() => DatabaseBackupFileLayout.Create([new("files", wrongRoot)], excluded));
        Assert.Throws<ArgumentException>(() => DatabaseBackupFileLayout.Create(
            Enumerable.Range(0, 33).Select(index => new DatabaseBackupFileRoot($"root-{index}", files)), excluded));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("file-storage/../escape")]
    [InlineData("file-storage//photos")]
    [InlineData("file-storage\\photos")]
    [InlineData("C:/absolute")]
    [InlineData("unknown/photos")]
    public void SerializedMappingsRejectUnsafeOrUndeclaredArchivePrefixes(string prefix)
        => Assert.Throws<InvalidDataException>(() => DatabaseBackupFileLayout.ValidateRootMappings([
            new("file-storage", "file-storage"), new("profile-photos", prefix)]));

    [Fact]
    public void SerializedMappingsAllowNestedAliasesButRejectDuplicateNames()
    {
        DatabaseBackupFileLayout.ValidateRootMappings([
            new("file-storage", "file-storage"), new("profile-photos", "file-storage/photos"),
            new("local-objects", "file-storage")]);
        Assert.Throws<InvalidDataException>(() => DatabaseBackupFileLayout.ValidateRootMappings([
            new("files", "files"), new("FILES", "files")]));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
