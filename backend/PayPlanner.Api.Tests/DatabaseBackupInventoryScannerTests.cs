using System.Text;
using System.Text.Json;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupInventoryScannerTests
{
    [Fact]
    public void PagesTraverseNestedFoldersAndAllHistoricalFormatsWithoutDuplicates()
    {
        WithRoot(root =>
        {
            foreach (var path in new[] { "root.sqlite", "z/last.sql", "a/second.dump", "a/first.backup", "a/nested/старый.zip" })
            {
                var full = Path.Combine(root, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, "fixture");
            }
            File.WriteAllText(Path.Combine(root, "root.sqlite.manifest.json"), "not verified by discovery");
            File.WriteAllText(Path.Combine(root, "ignore.txt"), "fixture");
            var reader = new TestReader();
            var scanner = new DatabaseBackupInventoryScanner(root, reader);
            string? cursor = null;
            var paths = new List<string>();
            for (var count = 0; count < 20; count++)
            {
                var page = scanner.Read(cursor, 1, CancellationToken.None);
                Assert.InRange(page.Items.Count, 0, 1);
                Assert.Empty(page.Issues);
                paths.AddRange(page.Items.Select(item => item.RelativePath));
                if (paths.Count == 1) Assert.True(page.Items[0].HasManifest);
                cursor = page.NextCursor;
                if (cursor is null) break;
            }
            Assert.Null(cursor);
            Assert.Equal(new[] { "root.sqlite", "a/first.backup", "a/second.dump", "a/nested/старый.zip", "z/last.sql" }, paths);
        });
    }

    [Fact]
    public void EmptyDirectoryBudgetReturnsAContinuationInsteadOfTraversingTheWholeTree()
    {
        WithRoot(root =>
        {
            for (var index = 0; index < 90; index++) Directory.CreateDirectory(Path.Combine(root, $"folder-{index:000}"));
            File.WriteAllText(Path.Combine(root, "folder-089", "old.zip"), "fixture");
            var scanner = new DatabaseBackupInventoryScanner(root, new TestReader());
            var first = scanner.Read(null, 20, CancellationToken.None);
            Assert.Empty(first.Items);
            Assert.NotNull(first.NextCursor);
            var page = first;
            for (var count = 0; page.NextCursor is not null && count < 20; count++)
                page = scanner.Read(page.NextCursor, 20, CancellationToken.None);
            Assert.Equal("folder-089/old.zip", Assert.Single(page.Items).RelativePath);
            Assert.Null(page.NextCursor);
        });
    }

    [Fact]
    public void UnreadableChildIsReportedAndOtherFoldersRemainSearchable()
    {
        WithRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "denied"));
            Directory.CreateDirectory(Path.Combine(root, "other"));
            File.WriteAllText(Path.Combine(root, "other", "old.zip"), "fixture");
            var scanner = new DatabaseBackupInventoryScanner(root, new TestReader { Denied = Path.Combine(root, "denied") });
            var page = scanner.Read(null, 20, CancellationToken.None);
            Assert.Equal("other/old.zip", Assert.Single(page.Items).RelativePath);
            Assert.Equal("denied", Assert.Single(page.Issues).RelativePath);
            Assert.Null(page.NextCursor);
        });
    }

    [Theory]
    [InlineData("bad-base64")]
    [InlineData("escaped")]
    [InlineData("empty")]
    public void InvalidOrEscapingCursorIsRejectedBeforeReadingDirectories(string value)
    {
        WithRoot(root =>
        {
            var reader = new TestReader();
            var cursor = value == "escaped" ? Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new { Version = 1, Frames = new[] { new { Directory = "../outside", After = "", Directories = false } } })))
                : value == "empty" ? Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"Version\":1,\"Frames\":[]}")) : value;
            Assert.Throws<ArgumentException>(() => new DatabaseBackupInventoryScanner(root, reader).Read(cursor, 20, CancellationToken.None));
            Assert.Equal(0, reader.Reads);
        });
    }

    [Fact]
    public void LimitsAndCancellationAreChecked()
    {
        WithRoot(root =>
        {
            var reader = new TestReader();
            var scanner = new DatabaseBackupInventoryScanner(root, reader);
            Assert.Throws<ArgumentException>(() => scanner.Read(null, 21, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => scanner.Read(null, 20, cancellation.Token));
            Assert.Equal(0, reader.Reads);
        });
    }

    private static void WithRoot(Action<string> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "payplanner-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { run(root); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestReader : IBackupDirectoryReader
    {
        public int Reads { get; private set; }
        public string? Denied { get; init; }
        public IEnumerable<string> EnumerateEntries(string directory)
        {
            Reads++;
            if (directory == Denied) throw new UnauthorizedAccessException();
            return Directory.EnumerateFileSystemEntries(directory).OrderByDescending(path => path, StringComparer.Ordinal);
        }
        public IReadOnlyList<string> GetFiles(string directory, string pattern) => Directory.GetFiles(directory, pattern);
        public IReadOnlyList<string> GetDirectories(string directory) => Directory.GetDirectories(directory);
        public bool IsEmpty(string directory) => !Directory.EnumerateFileSystemEntries(directory).Any();
    }
}
