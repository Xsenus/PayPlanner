using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupNativeBindingsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "payplanner-native-bindings-" + Guid.NewGuid().ToString("N"));
    private const string Part = "snapshots/test.fileset";
    private readonly DatabaseBackupFileBindingResolver resolver;
    private readonly byte[] payload = Encoding.UTF8.GetBytes("portable document evidence");

    public DatabaseBackupNativeBindingsTests()
    {
        var roots = new[]
        {
            new DatabaseBackupFileRoot("file-storage", Path.Combine(root, "files")),
            new DatabaseBackupFileRoot("profile-photos", Path.Combine(root, "files", "user-photos")),
            new DatabaseBackupFileRoot("profile-photos-legacy", Path.Combine(root, "legacy-photos")),
            new DatabaseBackupFileRoot("electronic-signatures", Path.Combine(root, "files", "signatures")),
            new DatabaseBackupFileRoot("electronic-signatures-legacy", Path.Combine(root, "legacy-signatures")),
        };
        foreach (var item in roots) Directory.CreateDirectory(item.Path);
        resolver = new(DatabaseBackupFileLayout.Create(roots, Path.Combine(root, "backups")));
    }

    [Theory]
    [InlineData("task-file")]
    [InlineData("case-document")]
    [InlineData("chat-attachment")]
    [InlineData("messenger-delivery")]
    [InlineData("esign-original")]
    [InlineData("esign-artifact")]
    [InlineData("profile-photo")]
    public async Task EveryNativeClassRetainsIdentityAndPayloadWithoutSourceDirectories(string kind)
    {
        var reference = Reference(kind);
        var inventory = Inventory(reference);
        var hash = Hash(inventory);
        var bytes = await CompleteAsync(reference, inventory);
        // No filesystem resolution is allowed during verification/restoration.
        DeleteOwnRoot();
        using var source = new MemoryStream(inventory);
        using var mapping = new MemoryStream(bytes);
        var calls = 0;
        await DatabaseBackupNativeBindings.VerifyAsync(source, mapping, hash, (part, entry, _) =>
        {
            Assert.Equal(Part, part);
            Assert.False(Path.IsPathRooted(entry));
            calls++;
            return Task.FromResult<DatabaseBackupArchiveFileHash?>(new(payload.Length, Hash(payload)));
        }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(source.CanRead);
        Assert.True(mapping.CanRead);
    }

    [Fact]
    public async Task PhotoRootSelectionIsFrozenBeforePackaging()
    {
        var reference = new DatabaseBackupStoredFileReference("profile-photo", 1, "/user-photos/photo.png", null, null, null, null);
        await File.WriteAllBytesAsync(Path.Combine(root, "legacy-photos", "photo.png"), payload);
        var inventory = Inventory(reference);
        using var input = new MemoryStream(inventory);
        using var plan = new MemoryStream();
        await DatabaseBackupNativeBindings.WritePlanAsync(input, plan, resolver, Part, Hash(inventory), CancellationToken.None);
        // A current file appearing later must not silently replace the selected legacy bytes.
        await File.WriteAllTextAsync(Path.Combine(root, "files", "user-photos", "photo.png"), "new current file");
        using var zipBytes = Archive("profile-photos-legacy/photo.png");
        using var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: true);
        plan.Position = 0;
        using var completed = new MemoryStream();
        await DatabaseBackupNativeBindings.SealAsync(plan, completed, zip, Hash(inventory), CancellationToken.None);
        completed.Position = 0;
        var bindings = new List<DatabaseBackupNativeBinding>();
        await foreach (var item in DatabaseBackupNativeBindings.ReadAsync(completed, Hash(inventory), true, CancellationToken.None)) bindings.Add(item);
        Assert.Equal("profile-photos-legacy/photo.png", Assert.Single(bindings).ArchiveEntryPath);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("footer")]
    [InlineData("count")]
    [InlineData("path")]
    [InlineData("part")]
    [InlineData("state")]
    [InlineData("field")]
    [InlineData("duplicate")]
    [InlineData("hash")]
    [InlineData("newline")]
    public async Task ReaderRejectsInvalidOrIncompleteMapping(string corruption)
    {
        var reference = Reference("task-file");
        var inventory = Inventory(reference);
        var lines = Encoding.UTF8.GetString(await CompleteAsync(reference, inventory)).Split('\n').ToList();
        switch (corruption)
        {
            case "header": lines.RemoveAt(0); break;
            case "footer": lines.RemoveAt(2); break;
            case "count": lines[2] = "{\"kind\":\"footer\",\"bindingCount\":2}"; break;
            case "state": lines[0] = lines[0].Replace("complete", "planned", StringComparison.Ordinal); break;
            case "duplicate": lines[0] = lines[0].Replace("\"kind\":\"header\"", "\"kind\":\"header\",\"kind\":\"header\"", StringComparison.Ordinal); break;
            case "newline": lines.RemoveAt(lines.Count - 1); break;
            default:
                var node = JsonNode.Parse(lines[1])!;
                if (corruption == "path") node["archiveEntryPath"] = "../escape.bin";
                else if (corruption == "part") node["archivePartRelativePath"] = "/outside.fileset";
                else if (corruption == "hash") node["actualSha256"] = "bad";
                else node["unexpected"] = true;
                lines[1] = node.ToJsonString();
                break;
        }
        using var mapping = new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in DatabaseBackupNativeBindings.ReadAsync(mapping, Hash(inventory), true, CancellationToken.None)) { }
        });
        Assert.True(mapping.CanRead);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("missing")]
    [InlineData("payload")]
    public async Task VerificationRequiresExactInventoryIdentityAndSavedBytes(string corruption)
    {
        var reference = Reference("task-file");
        var inventory = Inventory(reference);
        var bytes = await CompleteAsync(reference, inventory);
        if (corruption == "identity")
        {
            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            var node = JsonNode.Parse(lines[1])!;
            node["reference"]!["sourceId"] = 2;
            lines[1] = node.ToJsonString();
            bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines));
        }
        using var source = new MemoryStream(inventory);
        using var mapping = new MemoryStream(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupNativeBindings.VerifyAsync(source, mapping, Hash(inventory),
            (_, _, _) => Task.FromResult<DatabaseBackupArchiveFileHash?>(corruption == "missing" ? null
                : new(payload.Length, corruption == "payload" ? new string('a', 64) : Hash(payload))), CancellationToken.None));
    }

    [Fact]
    public async Task ExternalPhotosNeverInvokePayloadReader()
    {
        var reference = new DatabaseBackupStoredFileReference("profile-photo", 1, "https://external.invalid/photo.png", null, null, null, null);
        var inventory = Inventory(reference);
        var bytes = await CompleteAsync(reference, inventory);
        using var input = new MemoryStream(inventory);
        using var mapping = new MemoryStream(bytes);
        await DatabaseBackupNativeBindings.VerifyAsync(input, mapping, Hash(inventory),
            (_, _, _) => throw new InvalidOperationException("External URL must not be fetched."), CancellationToken.None);
    }

    [Fact]
    public async Task ShortReadsSplitUtf8WithoutLosingReferenceIdentity()
    {
        var reference = new DatabaseBackupStoredFileReference("profile-photo", 1,
            "https://external.invalid/фото.png?value=" + new string('x', 30000), null, null, null, null);
        var inventory = Inventory(reference);
        var lines = Encoding.UTF8.GetString(await CompleteAsync(reference, inventory)).Split('\n');
        lines[1] = JsonNode.Parse(lines[1])!.ToJsonString(new JsonSerializerOptions
        { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        using var source = new ShortReadStream(Encoding.UTF8.GetBytes(string.Join('\n', lines)), 1);
        using var input = new MemoryStream(inventory);
        await DatabaseBackupNativeBindings.VerifyAsync(input, source, Hash(inventory),
            (_, _, _) => throw new InvalidOperationException("No network request."), CancellationToken.None);
        Assert.True(source.CanRead);
        Assert.InRange(source.MaximumReadRequest, 1, 131072);
    }

    [Fact]
    public async Task OversizedUnfinishedLineFailsWithBoundedReadAhead()
    {
        using var source = new ShortReadStream(Encoding.UTF8.GetBytes("{\"kind\":\"header\",\"value\":\"" + new string('x', 140000)), 257);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in DatabaseBackupNativeBindings.ReadAsync(source, new string('a', 64), true, CancellationToken.None)) { }
        });
        Assert.Contains("превышает", error.Message, StringComparison.Ordinal);
        Assert.InRange(source.BytesRead, 1, 131072);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task CancellationDoesNotDisposeBorrowedStream()
    {
        using var source = new MemoryStream(new byte[] { (byte)'{' });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in DatabaseBackupNativeBindings.ReadAsync(source, new string('a', 64), true, cancellation.Token)) { }
        });
        Assert.True(source.CanRead);
    }

    private sealed class ShortReadStream(byte[] bytes, int chunk) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public int MaximumReadRequest { get; private set; }
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumReadRequest = Math.Max(MaximumReadRequest, buffer.Length);
            var count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
            BytesRead += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private DatabaseBackupStoredFileReference Reference(string kind)
    {
        var path = kind switch
        {
            "chat-attachment" => "doc.bin",
            "profile-photo" => "/user-photos/doc.bin",
            "esign-original" or "esign-artifact" => Path.Combine(root, "files", "signatures", "42", "doc.bin"),
            _ => "42/doc.bin",
        };
        if (kind == "profile-photo") File.WriteAllBytes(Path.Combine(root, "files", "user-photos", "doc.bin"), payload);
        return new(kind, 1, path, kind == "profile-photo" ? null : payload.Length,
            kind is "messenger-delivery" or "esign-original" or "esign-artifact" ? Hash(payload) : null,
            null, kind == "chat-attachment" ? 42 : null);
    }

    private async Task<byte[]> CompleteAsync(DatabaseBackupStoredFileReference reference, byte[] inventory)
    {
        using var input = new MemoryStream(inventory);
        using var plan = new MemoryStream();
        await DatabaseBackupNativeBindings.WritePlanAsync(input, plan, resolver, Part, Hash(inventory), CancellationToken.None);
        using var zipBytes = Archive(resolver.Resolve(reference).ArchiveEntryPath);
        using var zip = new ZipArchive(zipBytes, ZipArchiveMode.Read, leaveOpen: true);
        using var output = new MemoryStream();
        plan.Position = 0;
        await DatabaseBackupNativeBindings.SealAsync(plan, output, zip, Hash(inventory), CancellationToken.None);
        return output.ToArray();
    }

    private MemoryStream Archive(string? entryPath)
    {
        var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (entryPath is not null)
            {
                var entry = zip.CreateEntry(entryPath);
                using (var destination = entry.Open()) destination.Write(payload);
                entry.Comment = DatabaseBackupArchiveHash.ToComment(Hash(payload));
            }
        }
        bytes.Position = 0;
        return bytes;
    }

    private static byte[] Inventory(DatabaseBackupStoredFileReference reference)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            storedFiles = new[] { reference },
            storageReplicas = Array.Empty<DatabaseBackupStorageReplicaReference>(),
            storedFileCount = 1,
            storageReplicaCount = 0,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private void DeleteOwnRoot()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("payplanner-native-bindings-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid disposable test root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
    }
    public void Dispose() => DeleteOwnRoot();
}
