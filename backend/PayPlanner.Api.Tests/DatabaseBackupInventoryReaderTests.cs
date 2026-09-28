using System.Text;
using System.Text.Json;
using PayPlanner.Api.Services;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class DatabaseBackupInventoryReaderTests
{
    private static string Inventory(int count = 1) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        storedFiles = Enumerable.Range(1, count).Select(id => new DatabaseBackupStoredFileReference(
            "task-file", id, $"{id}/документ.txt", 7, null, 1, null)),
        storageReplicas = Array.Empty<DatabaseBackupStorageReplicaReference>(),
        storedFileCount = count,
        storageReplicaCount = 0
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(81920)]
    public async Task ReadsSplitUtf8RecordsAndValidatesFooterWithoutClosingCaller(int chunk)
    {
        using var source = new ShortReads(Encoding.UTF8.GetBytes(Inventory(12)), chunk);
        var rows = new List<DatabaseBackupInventoryRow>();
        await foreach (var item in DatabaseBackupInventoryReader.ReadAsync(source)) rows.Add(item);
        Assert.Equal(12, rows.Count);
        Assert.Equal("12/документ.txt", rows[^1].StoredFile!.StoredPath);
        Assert.True(source.CanRead);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("version")]
    [InlineData("truncated")]
    [InlineData("trailing")]
    [InlineData("unknown")]
    public async Task RejectsInvalidSchemaAndCounter(string corruption)
    {
        var json = Inventory();
        json = corruption switch
        {
            "count" => json.Replace("\"storedFileCount\":1", "\"storedFileCount\":2"),
            "duplicate" => json.Replace("\"formatVersion\":1", "\"formatVersion\":1,\"formatVersion\":1"),
            "missing" => json.Replace(",\"storageReplicaCount\":0", ""),
            "version" => json.Replace("\"formatVersion\":1", "\"formatVersion\":99"),
            "truncated" => json[..^1],
            "trailing" => json + "{}",
            _ => json.Replace("\"formatVersion\":1", "\"unexpected\":1")
        };
        using var source = new ShortReads(Encoding.UTF8.GetBytes(json), 7);
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source)) { }
        });
        Assert.True(error is InvalidDataException or JsonException);
        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task EarlyStopAndCancellationPreserveCallerAndBoundReadAhead()
    {
        using var source = new ShortReads(Encoding.UTF8.GetBytes(Inventory(2000)), 81920);
        await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source)) break;
        Assert.True(source.Position <= 65536);
        Assert.True(source.CanRead);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source, cancellation.Token)) { }
        });
    }

    private sealed class ShortReads(byte[] bytes, int chunk) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(chunk, buffer.Length)], cancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservesHeldDeletedGenerationAndExplicitMissingReplica(bool missingReplica)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(ReplicaInventory(missingReplica)));
        DatabaseBackupStorageReplicaReference? found = null;
        await foreach (var item in DatabaseBackupInventoryReader.ReadAsync(source)) found = item.Replica;
        Assert.NotNull(found);
        Assert.True(found.ObjectIsDeleted);
        Assert.True(found.LegalHold);
        Assert.Equal(missingReplica ? null : (long?)1, found.ReplicaId);
    }

    private static string ReplicaInventory(bool missing = false) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        storedFiles = Array.Empty<DatabaseBackupStoredFileReference>(),
        storageReplicas = new[] { new DatabaseBackupStorageReplicaReference(
            Guid.NewGuid(), Guid.NewGuid(), 2, 4, new string('a', 64), true, true, null,
            missing ? null : 1, missing ? null : "legacy-local", null, missing ? null : "files/document.txt",
            missing ? null : "available") },
        storedFileCount = 0,
        storageReplicaCount = 1
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Theory]
    [InlineData("missing-deleted")]
    [InlineData("bad-sha")]
    [InlineData("negative-size")]
    [InlineData("missing-provider")]
    [InlineData("orphan-replica")]
    [InlineData("extra-field")]
    [InlineData("duplicate-hold")]
    public async Task InvalidGenerationFactsAreRejected(string corruption)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ReplicaInventory())!;
        var item = node["storageReplicas"]![0]!.AsObject();
        switch (corruption)
        {
            case "missing-deleted": item.Remove("objectIsDeleted"); break;
            case "bad-sha": item["sha256"] = "short"; break;
            case "negative-size": item["sizeBytes"] = -1; break;
            case "missing-provider": item["providerKey"] = null; break;
            case "orphan-replica": item["replicaId"] = null; break;
            default: item["extra"] = true; break;
        }
        var json = node.ToJsonString();
        if (corruption == "duplicate-hold")
        {
            item.Remove("extra");
            json = node.ToJsonString().Replace("\"legalHold\":true", "\"legalHold\":true,\"legalHold\":false");
        }
        using var source = new ShortReads(Encoding.UTF8.GetBytes(json), 5);
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source)) { }
        });
        Assert.True(error is InvalidDataException or JsonException);
    }

    [Fact]
    public async Task OversizedSingleRecordCannotGrowTheBuffer()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Inventory())!;
        node["storedFiles"]![0]!["storedPath"] = new string('a', 70000);
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source)) { }
        });
        Assert.True(source.Position <= 65600);
    }

    [Fact]
    public async Task InvalidUtf8PropertyIsReportedAsInvalidInventory()
    {
        using var source = new MemoryStream([(byte)'{', (byte)'"', 0xff, (byte)'"', (byte)':', (byte)'1', (byte)'}']);
        var error = await Record.ExceptionAsync(async () =>
        {
            await foreach (var _ in DatabaseBackupInventoryReader.ReadAsync(source)) { }
        });
        Assert.True(error is InvalidDataException or JsonException);
    }

    [Fact]
    public async Task ExternalPhotoUrlIsPreservedWithoutTreatingItAsAFileSystemPath()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Inventory())!;
        var item = node["storedFiles"]![0]!;
        var url = "https://external.example.test/photo?token=" + new string('a', 30000);
        item["sourceKind"] = "profile-photo";
        item["storedPath"] = url;
        item["sizeBytes"] = null;
        item["version"] = null;
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
        var count = 0;
        await foreach (var row in DatabaseBackupInventoryReader.ReadAsync(source))
        {
            Assert.Equal(url, row.StoredFile!.StoredPath);
            count++;
        }
        Assert.Equal(1, count);
    }
}
