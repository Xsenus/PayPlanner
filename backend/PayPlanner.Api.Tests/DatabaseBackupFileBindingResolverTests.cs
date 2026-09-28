using PayPlanner.Api.Services;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupFileBindingResolverTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "payplanner-backup-binding-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> paths = new(StringComparer.Ordinal);
    private readonly DatabaseBackupFileBindingResolver resolver;

    public DatabaseBackupFileBindingResolverTests()
    {
        paths["file-storage"] = Path.Combine(root, "files");
        paths["profile-photos"] = Path.Combine(root, "files", "user-photos");
        paths["electronic-signatures"] = Path.Combine(root, "files", "signatures");
        paths["local-objects"] = Path.Combine(root, "files", "objects");
        paths["profile-photos-legacy"] = Path.Combine(root, "legacy-photos");
        paths["electronic-signatures-legacy"] = Path.Combine(root, "legacy-signatures");
        foreach (var path in paths.Values) Directory.CreateDirectory(path);
        resolver = new(DatabaseBackupFileLayout.Create(paths.Select(pair => new DatabaseBackupFileRoot(pair.Key, pair.Value)),
            Path.Combine(root, "backups")));
    }

    [Theory]
    [InlineData("task-file", "42/doc.pdf", null, "file-storage/task-files/42/doc.pdf")]
    [InlineData("case-document", "42/doc.pdf", null, "file-storage/case-documents/42/doc.pdf")]
    [InlineData("chat-attachment", "doc.pdf", 42, "file-storage/internal-chat-files/42/doc.pdf")]
    [InlineData("messenger-delivery", "2026/09/doc.pdf", null, "file-storage/messenger-document-deliveries/2026/09/doc.pdf")]
    [InlineData("esign-original", "42/doc.pdf", null, "file-storage/signatures/42/doc.pdf")]
    [InlineData("esign-artifact", "42/doc.pdf", 42, "file-storage/signatures/42/doc.pdf")]
    public void NativePathsUseActualNestedArchivePrefix(string kind, string storedPath, int? parentId, string entry)
    {
        var result = resolver.Resolve(new(kind, 1, storedPath, 5, null, 1, parentId));
        Assert.False(result.ExternalReference);
        Assert.Equal(entry, result.ArchiveEntryPath);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "files", entry["file-storage/".Length..])), result.PhysicalPath);
    }

    [Theory]
    [InlineData("/user-photos/photo.png")]
    [InlineData("https://crm.invalid/user-photos/photo.png?cache=123")]
    public void PhotoUsesCurrentThenLegacyWithoutFetchingUrl(string url)
    {
        File.WriteAllText(Path.Combine(paths["profile-photos-legacy"], "photo.png"), "old");
        var legacy = resolver.Resolve(new("profile-photo", 1, url, null, null, null, null));
        Assert.Equal("profile-photos-legacy/photo.png", legacy.ArchiveEntryPath);
        File.WriteAllText(Path.Combine(paths["profile-photos"], "photo.png"), "new");
        var current = resolver.Resolve(new("profile-photo", 1, url, null, null, null, null));
        Assert.Equal("file-storage/user-photos/photo.png", current.ArchiveEntryPath);
        Assert.False(current.ExternalReference);
    }

    [Fact]
    public void ExternalPhotoIsNotClaimedAsLocalPayload()
    {
        var result = resolver.Resolve(new("profile-photo", 1, "https://external.invalid/avatars/photo.png", null, null, null, null));
        Assert.True(result.ExternalReference);
        Assert.Null(result.PhysicalPath);
        Assert.Null(result.ArchiveEntryPath);
    }

    [Theory]
    [InlineData("esign-original")]
    [InlineData("esign-artifact")]
    public void AbsoluteLegacySignaturePrefersMigratedCurrentFile(string kind)
    {
        var legacyPath = Path.Combine(paths["electronic-signatures-legacy"], "document.pdf");
        File.WriteAllText(legacyPath, "legacy");
        var reference = new DatabaseBackupStoredFileReference(kind, 1, legacyPath, 6, null, null, null);
        Assert.Equal("electronic-signatures-legacy/document.pdf", resolver.Resolve(reference).ArchiveEntryPath);
        File.WriteAllText(Path.Combine(paths["electronic-signatures"], "document.pdf"), "current");
        Assert.Equal("file-storage/signatures/document.pdf", resolver.Resolve(reference).ArchiveEntryPath);
    }

    [Theory]
    [InlineData("esign-original")]
    [InlineData("esign-artifact")]
    public void SignatureCannotBorrowUnrelatedIncludedRoot(string kind)
    {
        var unrelated = Path.Combine(paths["file-storage"], "other.pdf");
        File.WriteAllText(unrelated, "not a signature");
        Assert.Throws<InvalidDataException>(() => resolver.Resolve(new(kind, 1, unrelated, null, null, null, null)));
    }

    [Theory]
    [InlineData("local", "a/b.bin", "file-storage/objects/a/b.bin")]
    [InlineData("legacy-local", "files/task-files/42/doc.pdf", "file-storage/task-files/42/doc.pdf")]
    [InlineData("legacy-local", "profile-current/photo.png", "file-storage/user-photos/photo.png")]
    [InlineData("legacy-local", "profile-legacy/photo.png", "profile-photos-legacy/photo.png")]
    [InlineData("legacy-local", "esign-current/doc.pdf", "file-storage/signatures/doc.pdf")]
    [InlineData("legacy-local", "esign-legacy/doc.pdf", "electronic-signatures-legacy/doc.pdf")]
    public void LocalManagedLocatorPreservesLogicalRoot(string provider, string locator, string entry)
    {
        var result = resolver.ResolveLocalReplica(Replica(provider, locator));
        Assert.Equal(entry, result.ArchiveEntryPath);
        Assert.False(result.ExternalReference);
    }

    [Theory]
    [InlineData("task-file", "../doc.pdf", null)]
    [InlineData("case-document", "C:\\foreign\\doc.pdf", null)]
    [InlineData("chat-attachment", "../doc.pdf", 42)]
    [InlineData("chat-attachment", "nested/doc.pdf", 42)]
    [InlineData("chat-attachment", "doc.pdf", null)]
    [InlineData("messenger-delivery", "../doc.pdf", null)]
    [InlineData("profile-photo", "/user-photos/../doc.pdf", null)]
    [InlineData("profile-photo", "file:///private/photo.png", null)]
    [InlineData("unknown", "doc.pdf", null)]
    public void UnsafeNativePathsAreRejected(string kind, string path, int? parent)
        => Assert.Throws<InvalidDataException>(() => resolver.Resolve(new(kind, 1, path, null, null, null, parent)));

    [Theory]
    [InlineData("local", "../private.bin")]
    [InlineData("legacy-local", "files/../private.bin")]
    [InlineData("legacy-local", "unknown/private.bin")]
    [InlineData("s3", "bucket/key")]
    public void UnknownExternalOrUnsafeReplicaCannotBeReadAsLocal(string provider, string locator)
        => Assert.Throws<InvalidDataException>(() => resolver.ResolveLocalReplica(Replica(provider, locator)));

    private static DatabaseBackupStorageReplicaReference Replica(string provider, string locator)
        => new(Guid.NewGuid(), Guid.NewGuid(), 1, 5, new string('a', 64), true, true, null,
            1, provider, null, locator, "deletePending");

    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("size")]
    [InlineData("hash")]
    [InlineData("wrong-root")]
    public async Task NativeCoverageRequiresExactEntryAndSnapshotFacts(string corruption)
    {
        var payload = Encoding.UTF8.GetBytes("document evidence");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        var reference = new DatabaseBackupStoredFileReference("messenger-delivery", 1, "2026/09/document.pdf",
            corruption == "size" ? payload.Length + 1 : payload.Length,
            corruption == "hash" ? new string('a', 64) : hash, 1, null);
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (corruption != "missing")
            {
                var entry = archive.CreateEntry(corruption == "wrong-root"
                    ? "file-storage/task-files/2026/09/document.pdf"
                    : "file-storage/messenger-document-deliveries/2026/09/document.pdf");
                using (var destination = entry.Open()) await destination.WriteAsync(payload);
                entry.Comment = DatabaseBackupArchiveHash.ToComment(hash);
            }
        }
        archiveBytes.Position = 0;
        using var readArchive = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true);
        using var inventory = Inventory(reference);
        if (corruption == "valid")
            await DatabaseBackupNativeFileCoverage.ValidateAsync(inventory, readArchive, resolver, CancellationToken.None);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                DatabaseBackupNativeFileCoverage.ValidateAsync(inventory, readArchive, resolver, CancellationToken.None));
        Assert.True(inventory.CanRead);
        Assert.True(archiveBytes.CanRead);
    }

    [Fact]
    public async Task ExternalPhotoCanRemainAReferenceWithoutAnyArchive()
    {
        using var inventory = Inventory(new("profile-photo", 1, "https://external.invalid/avatar.png", null, null, null, null));
        await DatabaseBackupNativeFileCoverage.ValidateAsync(inventory, null, resolver, CancellationToken.None);
    }

    [Theory]
    [InlineData("task-file", "42/doc.bin", null)]
    [InlineData("case-document", "42/doc.bin", null)]
    [InlineData("chat-attachment", "doc.bin", 42)]
    [InlineData("messenger-delivery", "2026/09/doc.bin", null)]
    [InlineData("esign-original", "42/doc.bin", null)]
    [InlineData("esign-artifact", "42/doc.bin", 42)]
    [InlineData("profile-photo", "/user-photos/doc.bin", null)]
    public async Task EveryNativeClassIsCoveredByItsArchivedPayload(string kind, string path, int? parent)
    {
        if (kind == "profile-photo") await File.WriteAllTextAsync(Path.Combine(paths["profile-photos"], "doc.bin"), "photo");
        var reference = new DatabaseBackupStoredFileReference(kind, 1, path, 0, null, null, parent);
        var binding = resolver.Resolve(reference);
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(binding.ArchiveEntryPath!);
            using (entry.Open()) { }
            entry.Comment = DatabaseBackupArchiveHash.ToComment(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())));
        }
        archiveBytes.Position = 0;
        using var readArchive = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true);
        using var inventory = Inventory(reference);
        await DatabaseBackupNativeFileCoverage.ValidateAsync(inventory, readArchive, resolver, CancellationToken.None);
    }

    private static MemoryStream Inventory(DatabaseBackupStoredFileReference reference)
        => new(JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            storedFiles = new[] { reference },
            storageReplicas = Array.Empty<DatabaseBackupStorageReplicaReference>(),
            storedFileCount = 1,
            storageReplicaCount = 0,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("payplanner-backup-binding-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid disposable test root.");
        Directory.Delete(resolved, recursive: true);
    }
}
