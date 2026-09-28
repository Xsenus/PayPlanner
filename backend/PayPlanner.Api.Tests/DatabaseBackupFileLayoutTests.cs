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
    public void ParentReleaseLinkIsPinnedEvenWhenDeploymentSwitchesAlias()
    {
        var first = DirectoryPath("release-a");
        var second = DirectoryPath("release-b");
        Directory.CreateDirectory(Path.Combine(first, "files"));
        Directory.CreateDirectory(Path.Combine(second, "files"));
        File.WriteAllText(Path.Combine(first, "files", "evidence.txt"), "first");
        File.WriteAllText(Path.Combine(second, "files", "evidence.txt"), "second");
        var alias = Path.Combine(root, "current");
        CreateDirectoryLink(alias, first);
        try
        {
            var layout = DatabaseBackupFileLayout.Create([new("files", Path.Combine(alias, "files"), true)],
                DirectoryPath("backups"));
            Directory.Delete(alias);
            CreateDirectoryLink(alias, second);
            var pinned = layout.GetPhysicalFilePath("files", "evidence.txt");
            Assert.Equal(Path.GetFullPath(Path.Combine(first, "files", "evidence.txt")), pinned);
            Assert.Equal("first", File.ReadAllText(pinned));
            Assert.Equal("files/evidence.txt", layout.GetArchiveEntryPath(Path.Combine(alias, "files", "evidence.txt")));
            Assert.Equal(Path.GetFullPath(Path.Combine(first, "files")), Assert.Single(layout.ArchiveRoots).Path);
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void ExcludedBackupDirectoryIsComparedByPinnedPhysicalPath()
    {
        var release = DirectoryPath("release");
        var physicalFiles = Path.Combine(release, "files");
        var physicalBackups = Path.Combine(physicalFiles, "backups");
        Directory.CreateDirectory(physicalBackups);
        var alias = Path.Combine(root, "current");
        CreateDirectoryLink(alias, release);
        try
        {
            var layout = DatabaseBackupFileLayout.Create([new("files", Path.Combine(alias, "files"), true)],
                Path.Combine(alias, "files", "backups"));
            Assert.True(layout.IsExcludedPhysicalPath(Path.Combine(physicalBackups, "old.backup")));
            Assert.False(layout.IsExcludedPhysicalPath(Path.Combine(physicalFiles, "evidence.txt")));
            Assert.Throws<InvalidDataException>(() => layout.GetArchiveEntryPath(Path.Combine(alias, "files", "backups", "old.backup")));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void LinkedAncestorsInsideReleaseTargetArePinnedRecursively()
    {
        var first = DirectoryPath("target-a");
        var second = DirectoryPath("target-b");
        Directory.CreateDirectory(Path.Combine(first, "app", "files"));
        Directory.CreateDirectory(Path.Combine(second, "app", "files"));
        File.WriteAllText(Path.Combine(first, "app", "files", "evidence.txt"), "first");
        File.WriteAllText(Path.Combine(second, "app", "files", "evidence.txt"), "second");
        var parent = Path.Combine(root, "target-parent");
        var current = Path.Combine(root, "current");
        CreateDirectoryLink(parent, first);
        CreateDirectoryLink(current, Path.Combine(parent, "app"));
        try
        {
            var layout = DatabaseBackupFileLayout.Create([new("files", Path.Combine(current, "files"), true)],
                DirectoryPath("backups"));
            Directory.Delete(parent);
            CreateDirectoryLink(parent, second);
            var path = layout.GetPhysicalFilePath("files", "evidence.txt");
            Assert.Equal(Path.GetFullPath(Path.Combine(first, "app", "files", "evidence.txt")), path);
            Assert.Equal("first", File.ReadAllText(path));
        }
        finally { Directory.Delete(current); Directory.Delete(parent); }
    }

    [Fact]
    public void RootItselfCannotBeReplacedWithDirectoryLink()
    {
        var target = DirectoryPath("target");
        var alias = Path.Combine(root, "root-link");
        CreateDirectoryLink(alias, target);
        try
        {
            Assert.Throws<IOException>(() => DatabaseBackupFileLayout.Create([new("files", alias, true)], DirectoryPath("backups")));
        }
        finally { Directory.Delete(alias); }
    }

    private static void CreateDirectoryLink(string alias, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(alias, target); return; }
        // Junctions exercise Windows parent-link resolution without requiring symlink privileges.
        var startInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "/c", "mklink", "/J", alias, target }) startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(5000), "Directory link creation timed out.");
        Assert.True(process.ExitCode == 0, output + error);
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
    public void LogicalAliasesResolveAgainstFrozenPhysicalPathsWithoutDuplicateBytes()
    {
        var files = DirectoryPath("files");
        var photos = DirectoryPath("files/photos");
        var layout = DatabaseBackupFileLayout.Create([
            new("file-storage", files), new("profile-photos", photos), new("local-objects", files)],
            Path.Combine(files, "backups"));
        var photo = layout.GetPhysicalFilePath("profile-photos", "41\\image.png");
        Assert.Equal(Path.GetFullPath(Path.Combine(photos, "41", "image.png")), photo);
        Assert.Equal("file-storage/photos/41/image.png", layout.GetArchiveEntryPath(photo));
        Assert.Equal(layout.GetPhysicalFilePath("file-storage", "objects/file.bin"),
            layout.GetPhysicalFilePath("local-objects", "objects/file.bin"));
        Assert.Throws<InvalidDataException>(() => layout.GetPhysicalFilePath("profile-photos-legacy", "image.png"));
        Assert.Throws<InvalidDataException>(() => layout.GetPhysicalFilePath("file-storage", "backups/dump.sqlite"));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("folder/../outside")]
    [InlineData("folder\\..\\outside")]
    [InlineData("/absolute")]
    [InlineData("C:\\absolute")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("folder//file")]
    [InlineData(".")]
    [InlineData("file:stream")]
    public void LogicalReferencesCannotEscapeOrCreateAmbiguousPaths(string relative)
    {
        var files = DirectoryPath("files");
        var layout = DatabaseBackupFileLayout.Create([new("file-storage", files)], Path.Combine(root, "backups"));
        Assert.Throws<InvalidDataException>(() => layout.GetPhysicalFilePath("file-storage", relative));
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
