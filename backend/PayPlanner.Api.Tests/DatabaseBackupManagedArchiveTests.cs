using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupManagedArchiveTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"payplanner-managed-backup-{Guid.NewGuid():N}");
    private static readonly byte[] Payload = Enumerable.Range(0, 180000).Select(value => (byte)(value % 251)).ToArray();
    private static DatabaseBackupStorageReplicaReference Replica(int generation, long? id = 1, string? status = "available")
        => new(Guid.Parse($"00000000-0000-0000-0000-{generation:000000000000}"),
            Guid.Parse("10000000-0000-0000-0000-000000000001"), generation, Payload.Length,
            Convert.ToHexString(SHA256.HashData(Payload)), true, true, new DateTime(2030, 1, 1),
            id, id is null ? null : "s3", id is null ? null : "test-connection",
            id is null ? null : "test-connection/generation.bin", id is null ? null : status);

    [Fact]
    public async Task EveryGenerationIncludingDeletedAndHeldIsSealedAndReadableWithoutSources()
    {
        var rows = new[] { Replica(1), Replica(1, 2), Replica(2), Replica(3) };
        var opened = new List<long>();
        var sources = new List<MemoryStream>();
        var bindings = new List<DatabaseBackupManagedBinding>();
        using var inventory = Inventory(rows);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            var count = await DatabaseBackupManagedArchive.WriteAsync(inventory, archive, "set.managedset", root,
                (replica, _) =>
                {
                    opened.Add(replica.ReplicaId!.Value);
                    var source = new MemoryStream(Payload);
                    sources.Add(source);
                    return Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, replica.Locator!, source, Payload.Length));
                }, (binding, _) => { bindings.Add(binding); return Task.CompletedTask; }, CancellationToken.None);
            Assert.Equal(3, count);
        }
        Assert.Equal(3, opened.Count); // No second download once a valid candidate has been frozen.
        Assert.All(sources, source => Assert.False(source.CanRead));
        Assert.Empty(Directory.EnumerateFiles(root));
        Assert.True(inventory.CanRead);
        Assert.True(bytes.CanWrite);
        bytes.Position = 0;
        using var restored = new ZipArchive(bytes, ZipArchiveMode.Read, true);
        Assert.Equal(3, restored.Entries.Count);
        foreach (var binding in bindings)
        {
            Assert.True(binding.SelectedReplica.ObjectIsDeleted);
            Assert.True(binding.SelectedReplica.LegalHold);
            Assert.Equal("set.managedset", binding.ArchivePartRelativePath);
            var entry = restored.GetEntry(binding.ArchiveEntryPath)!;
            await using var content = entry.Open();
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy);
            Assert.Equal(Payload, copy.ToArray());
            Assert.Equal(binding.ActualSizeBytes, entry.Length);
            Assert.Equal(binding.ActualSha256, DatabaseBackupArchiveHash.FromComment(entry.Comment));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unavailable")]
    [InlineData("hash")]
    [InlineData("short")]
    [InlineData("oversize")]
    [InlineData("metadata-size")]
    [InlineData("io")]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("source-cancellation")]
    public async Task BadPrimaryFallsBackWithoutArchivingItsBytes(string failure)
    {
        var bindings = new List<DatabaseBackupManagedBinding>();
        var attempts = 0;
        using var inventory = Inventory([Replica(1), Replica(1, 2)]);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            await DatabaseBackupManagedArchive.WriteAsync(inventory, archive, "set.managedset", root, (reference, _) =>
            {
                attempts++;
                if (reference.ReplicaId == 2) return Read(Payload);
                return failure switch
                {
                    "missing" => Task.FromResult(new StorageReadResult(StorageOperationStatus.NotFound, "missing", null, 0)),
                    "unavailable" => Task.FromResult(new StorageReadResult(StorageOperationStatus.Unavailable, "unavailable", null, 0)),
                    "hash" => Read(new byte[Payload.Length]),
                    "short" => Read(Payload[..^1]),
                    "oversize" => Read([.. Payload, (byte)1]),
                    "metadata-size" => Read(Payload, Payload.Length + 1),
                    "io" => throw new IOException("source unavailable"),
                    "http" => throw new HttpRequestException("source unavailable"),
                    "timeout" => throw new TimeoutException("source timeout"),
                    "source-cancellation" => throw new OperationCanceledException("provider timeout"),
                    _ => throw new InvalidOperationException()
                };
            }, (binding, _) => { bindings.Add(binding); return Task.CompletedTask; }, CancellationToken.None);
        }
        Assert.Equal(2, attempts);
        Assert.Equal(2, Assert.Single(bindings).SelectedReplica.ReplicaId);
        Assert.Empty(Directory.EnumerateFiles(root));
        bytes.Position = 0;
        using var restored = new ZipArchive(bytes, ZipArchiveMode.Read);
        var entry = Assert.Single(restored.Entries);
        await using var content = entry.Open();
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy);
        Assert.Equal(Payload, copy.ToArray());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("pending")]
    [InlineData("deleted")]
    [InlineData("corrupt")]
    public async Task NoValidCandidateFailsWithoutPretendingGenerationWasSaved(string status)
    {
        using var inventory = Inventory([status == "none" ? Replica(1, null) : Replica(1, 1, status)]);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
        var opened = false;
        var mapped = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupManagedArchive.WriteAsync(
            inventory, archive, "set.managedset", root,
            (_, _) => { opened = true; return Read(Payload); },
            (_, _) => { mapped = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.False(opened);
        Assert.False(mapped);
        Assert.Empty(Directory.EnumerateFiles(root));
    }

    [Theory]
    [InlineData("facts")]
    [InlineData("duplicate-replica")]
    [InlineData("duplicate-generation")]
    [InlineData("order")]
    public async Task ContradictoryOrDuplicateInventoryCannotComplete(string corruption)
    {
        var rows = corruption switch
        {
            "facts" => new[] { Replica(1), Replica(1, 2) with { LegalHold = false } },
            "duplicate-replica" => new[] { Replica(1), Replica(1) },
            "duplicate-generation" => new[] { Replica(1), Replica(2), Replica(1) },
            "order" => new[] { Replica(2), Replica(1) },
            _ => throw new InvalidOperationException()
        };
        using var inventory = Inventory(rows);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupManagedArchive.WriteAsync(
            inventory, archive, "set.managedset", root, (_, _) => Read(Payload),
            (_, _) => Task.CompletedTask, CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(root));
    }

    [Fact]
    public async Task CancellationDuringSourceReadCleansSpoolAndDoesNotTryAnotherCandidate()
    {
        using var inventory = Inventory([Replica(1), Replica(1, 2)]);
        using var bytes = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatabaseBackupManagedArchive.WriteAsync(
            inventory, archive, "set.managedset", root, (_, _) =>
            {
                attempts++;
                cancellation.Cancel();
                return Read(Payload);
            }, (_, _) => Task.CompletedTask, cancellation.Token));
        Assert.Equal(1, attempts);
        Assert.Empty(Directory.EnumerateFiles(root));
        Assert.True(inventory.CanRead);
    }

    [Fact]
    public async Task EmptyCatalogHasNoDownloadsOrBindings()
    {
        using var inventory = Inventory([]);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
        Assert.Equal(0, await DatabaseBackupManagedArchive.WriteAsync(inventory, archive, "set.managedset", root,
            (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(root));
    }

    [Fact]
    public async Task MappingFailureDoesNotDeleteForeignFileOrLeaveSpool()
    {
        using var inventory = Inventory([Replica(1)]);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        var foreign = Path.Combine(root, "foreign.txt");
        await File.WriteAllTextAsync(foreign, "must survive");
        using var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true);
        await Assert.ThrowsAsync<IOException>(() => DatabaseBackupManagedArchive.WriteAsync(inventory, archive,
            "set.managedset", root, (_, _) => Read(Payload), (_, _) => throw new IOException("mapping failure"), CancellationToken.None));
        Assert.Equal(foreign, Assert.Single(Directory.EnumerateFiles(root)));
        Assert.Equal("must survive", await File.ReadAllTextAsync(foreign));
    }

    [Fact]
    public async Task EmptyGenerationStillHasVerifiedArchiveEntryAndBinding()
    {
        var reference = Replica(1) with { SizeBytes = 0, Sha256 = Convert.ToHexString(SHA256.HashData([])) };
        using var inventory = Inventory([reference]);
        using var bytes = new MemoryStream();
        Directory.CreateDirectory(root);
        var bindings = new List<DatabaseBackupManagedBinding>();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
            Assert.Equal(1, await DatabaseBackupManagedArchive.WriteAsync(inventory, archive, "set.managedset", root,
                (_, _) => Read([], 0), (binding, _) => { bindings.Add(binding); return Task.CompletedTask; }, CancellationToken.None));
        Assert.Equal(0, Assert.Single(bindings).ActualSizeBytes);
        bytes.Position = 0;
        using var restored = new ZipArchive(bytes, ZipArchiveMode.Read);
        Assert.Equal(0, Assert.Single(restored.Entries).Length);
    }

    [Fact]
    public async Task SizeBoundStopsWritingBeforeOverflowingStagingBudget()
    {
        using var source = new MemoryStream(new byte[90000]);
        using var destination = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackupArchiveHash.CopyAsync(source,
            destination, CancellationToken.None, 81920));
        Assert.Equal(81920, destination.Length);
        Assert.True(source.CanRead);
        Assert.True(destination.CanWrite);
        using var negativeSource = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DatabaseBackupArchiveHash.CopyAsync(
            negativeSource, destination, CancellationToken.None, -1));
    }

    private static Task<StorageReadResult> Read(byte[] bytes, long? advertisedSize = null)
        => Task.FromResult(new StorageReadResult(StorageOperationStatus.Succeeded, "test", new MemoryStream(bytes),
            advertisedSize ?? Payload.Length));

    private static MemoryStream Inventory(DatabaseBackupStorageReplicaReference[] replicas)
        => new(JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            storedFiles = Array.Empty<DatabaseBackupStoredFileReference>(),
            storageReplicas = replicas,
            storedFileCount = 0,
            storageReplicaCount = replicas.Length
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    public void Dispose()
    {
        var resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("payplanner-managed-backup-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid owned test directory.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
