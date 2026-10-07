using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class YandexDiskNativeWireTests
{
    private static readonly byte[] Bytes = Enumerable.Range(0, 65539).Select(i => (byte)(i % 251)).ToArray();
    private static readonly string Hash = Convert.ToHexString(SHA256.HashData(Bytes)).ToLowerInvariant();

    [Fact]
    public async Task NativeCapacityObservesFreshQuotaAndFailsClosedOnCloudError()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Endpoint.TotalCapacityBytes = 1000;
        fixture.Endpoint.UsedCapacityBytes = 200;
        var first = await fixture.Yandex.ObserveCapacityAsync("owned-resource", "default", 50, CancellationToken.None);
        Assert.Equal(800, first.AvailableBytes);
        Assert.Equal("yandex-disk-account:128280859", first.GroupId);
        fixture.Endpoint.UsedCapacityBytes = 950;
        var second = await fixture.Yandex.ObserveCapacityAsync("owned-resource", "default", 50, CancellationToken.None);
        Assert.Equal(50, second.AvailableBytes);
        Assert.False(StorageCapacityBudget.Evaluate([second], [], [new("owned-resource", 1, "owned-write")],
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)).CanSchedule);
        fixture.Endpoint.FailCapacity = true;
        Assert.Null((await fixture.Yandex.ObserveCapacityAsync("owned-resource", "default", 50, CancellationToken.None)).AvailableBytes);
        Assert.Equal(0, fixture.Endpoint.PutAttempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeadOfChunkedDownloadDoesNotInventZeroLength(bool omitResourceSize)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Endpoint.SetBytes("probe.bin", Bytes);
        fixture.Endpoint.OmitContentLength = true;
        fixture.Endpoint.OmitResourceSize = omitResourceSize;
        var metadata = await fixture.Yandex.HeadAsync("default/probe.bin", CancellationToken.None);
        Assert.Equal(omitResourceSize ? StorageOperationStatus.Unavailable : StorageOperationStatus.Succeeded, metadata.Status);
        if (!omitResourceSize) { Assert.Equal(Bytes.Length, metadata.SizeBytes); Assert.Equal(Hash, metadata.Sha256); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteWithLostAcknowledgementIsUnknownAndDoesNotLoseStoredBytes(bool disconnect)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Endpoint.FailNextPutAfterCommit = !disconnect;
        fixture.Endpoint.DropNextPutResponse = disconnect;
        using var content = new MemoryStream(Bytes, false);
        var result = await fixture.Yandex.PutAsync(new("default/lost.bin", content, "application/octet-stream", Bytes.Length, Hash), CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Unknown, result.Status);
        Assert.Equal(Bytes, fixture.Endpoint.GetBytes("lost.bin"));
        Assert.Equal(1, fixture.Endpoint.PutCount);
        Assert.Equal(1, fixture.Endpoint.PutAttempts);
        var metadata = await fixture.Yandex.HeadAsync("default/lost.bin", CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Succeeded, metadata.Status);
        Assert.Equal(Hash, metadata.Sha256);
        Assert.True(content.CanRead); // Native StreamContent must not own the caller's stream.
    }

    [Fact]
    public async Task NativeReadsAndPreUploadFailureAreUnavailableRatherThanLostOrCorrupt()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Endpoint.SetBytes("probe.bin", Bytes);
        fixture.Endpoint.FailDownload = true;
        Assert.Equal(StorageOperationStatus.Unavailable, (await fixture.Yandex.HeadAsync("default/probe.bin", CancellationToken.None)).Status);
        fixture.Endpoint.FailUploadLink = true;
        using var content = new MemoryStream(Bytes, false);
        Assert.Equal(StorageOperationStatus.Unavailable, (await fixture.Yandex.PutAsync(new("default/unwritten.bin", content, "application/octet-stream", Bytes.Length, Hash), CancellationToken.None)).Status);
        Assert.Equal(0, fixture.Endpoint.PutCount);
    }

    [Fact]
    public async Task HeadCountsActualBytesEvenIfChunkedResourceMetadataChanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Endpoint.SetBytes("probe.bin", Bytes);
        fixture.Endpoint.OmitContentLength = true;
        fixture.Endpoint.ResourceSizeOverride = Bytes.Length + 3;
        var metadata = await fixture.Yandex.HeadAsync("default/probe.bin", CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Succeeded, metadata.Status);
        Assert.Equal(Bytes.Length, metadata.SizeBytes);
        Assert.Equal(Hash, metadata.Sha256);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadDisconnectOrTruncationDoesNotDamageCatalogState(bool duringBody)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("none");
        var original = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "yandex-disk");
        fixture.Endpoint.TruncateDownloads = duringBody;
        fixture.Endpoint.DropDownloads = !duringBody;
        var report = await new StorageReconciliationService(fixture.Db, fixture.Registry).RunAsync(true, CancellationToken.None);
        Assert.Contains(report.Issues, issue => issue.Kind == "verificationUnavailable");
        Assert.Equal(0, report.CorruptReplicas);
        Assert.Equal(0, report.MissingReplicas);
        Assert.Equal(0, report.RepairOperationsQueued);
        Assert.Equal(0, fixture.Endpoint.PutAttempts);
        var retained = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == original.Id);
        Assert.Equal(original.Status, retained.Status);
        Assert.Equal(original.UpdatedAt, retained.UpdatedAt);
        Assert.Equal(original.LastVerifiedAt, retained.LastVerifiedAt);
        Assert.Equal(original.LastError, retained.LastError);
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("short")]
    [InlineData("truncated")]
    public async Task ManagedReadRejectsNativeBytesChangedAfterCatalogVerification(string failure)
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await fixture.SeedAsync("none");
        fixture.Options.CurrentValue.DefaultPolicy.ReadProviders = ["yandex-disk", "local"];
        if (failure == "truncated") fixture.Endpoint.TruncateDownloads = true;
        else fixture.Endpoint.SetBytes("yandex.bin", failure == "short" ? Bytes[..^1] : Fixture.DamagedBytes);

        var storage = new StorageObjectService(fixture.Db,
            new StoragePolicyService(fixture.Options, fixture.Registry), fixture.Registry, fixture.Options);
        await using (var read = await storage.OpenReadAsync(generation.StorageObjectId, CancellationToken.None))
        {
            Assert.NotNull(read);
            Assert.Equal("local", read.ProviderKey);
            using var received = new MemoryStream();
            await read.Content.CopyToAsync(received);
            Assert.Equal(Bytes, received.ToArray());
        }
        Assert.Equal(StorageReplicaStatuses.Suspect,
            (await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "yandex-disk")).Status);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{generation.StorageObjectId:N}_*.tmp"));
    }

    [Fact]
    public async Task CallerCancellationAfterNativeCommitIsNotPublishedAndRetryReadsReceipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("yandex-disk");
        var source = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "local");
        Assert.Equal(1, (await new StorageReconciliationService(fixture.Db, fixture.Registry).RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        fixture.Endpoint.BlockPutAcknowledgement = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var processing = fixture.Processor.ProcessBatchAsync("owned-cancelled-native", cancellation.Token);
        try
        {
            await fixture.Endpoint.PutCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        }
        finally { fixture.Endpoint.ReleasePutAcknowledgement.TrySetResult(true); }
        var operation = await fixture.Db.StorageOperations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageOperationStatuses.Running, operation.Status);
        Assert.Equal(1, fixture.Endpoint.PutAttempts);
        Assert.Single(await fixture.Db.StorageReplicas.Where(r => r.Status == StorageReplicaStatuses.RepairPending).ToArrayAsync());
        fixture.Db.Clients.Add(new Client { Name = "Unrelated after cancelled native PUT" });
        await fixture.Db.SaveChangesAsync();
        Assert.Equal(0, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.RetainedCorrupt));
        await fixture.Local.DeleteAsync(source.Locator, CancellationToken.None);
        await fixture.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.LeaseExpiresAt, DateTime.UtcNow.AddSeconds(-1)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await fixture.Processor.ProcessBatchAsync("owned-retry-after-cancel", CancellationToken.None)).Succeeded);
        Assert.Equal(1, fixture.Endpoint.PutAttempts);
    }

    [Theory]
    [InlineData("yandex-disk", false, false)]
    [InlineData("yandex-disk", true, false)]
    [InlineData("local", false, false)]
    [InlineData("local", true, false)]
    [InlineData("yandex-disk", false, true)]
    [InlineData("yandex-disk", true, true)]
    [InlineData("local", false, true)]
    [InlineData("local", true, true)]
    public async Task NativeRepairRetainsOriginalAndReadsExactReplacement(string damagedProvider, bool legacy, bool chunked)
    {
        await using var fixture = await Fixture.CreateAsync();
        var generation = await fixture.SeedAsync(damagedProvider);
        fixture.Endpoint.OmitContentLength = chunked;
        var original = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == damagedProvider);
        var source = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey != damagedProvider);
        var scan = new StorageReconciliationService(fixture.Db, fixture.Registry);
        if (legacy)
        {
            fixture.Db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = generation.StorageObjectId,
                StorageObjectGenerationId = generation.Id,
                OperationType = StorageOperationTypes.Repair,
                Status = StorageOperationStatuses.Failed,
                ProviderKey = original.ProviderKey,
                ConnectionId = original.ConnectionId,
                IdempotencyKey = $"reconcile:repair:{generation.Id:N}:{original.Id}",
                PayloadJson = JsonSerializer.Serialize(new StorageOperationPayload(original.Locator, source.ProviderKey, source.Locator), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            });
            await fixture.Db.SaveChangesAsync(); fixture.Db.ChangeTracker.Clear();
        }
        else Assert.Equal(1, (await scan.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        Assert.Equal(0, (await scan.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        var result = await fixture.Processor.ProcessBatchAsync("owned-native-yandex-repair", CancellationToken.None);
        Assert.Equal(1, result.Succeeded);
        var operation = await fixture.Db.StorageOperations.AsNoTracking().SingleAsync();
        var plan = JsonSerializer.Deserialize<StorageOperationPayload>(operation.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(original.Locator, plan.OriginalTargetLocator);
        Assert.NotEqual(original.Locator, plan.TargetLocator);
        Assert.Equal(StorageReplicaStatuses.RetainedCorrupt, (await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == original.Id)).Status);
        Assert.Equal(Fixture.DamagedBytes, await ReadBytesAsync(fixture.Registry.GetRequired(damagedProvider), original.Locator));
        Assert.Equal(Bytes, await ReadBytesAsync(fixture.Registry.GetRequired(damagedProvider), plan.TargetLocator));
        await fixture.Db.StorageReplicas.Where(r => r.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Missing));
        var storage = new StorageObjectService(fixture.Db, new StoragePolicyService(fixture.Options, fixture.Registry), fixture.Registry, fixture.Options);
        var read = await storage.OpenReadAsync(generation.StorageObjectId, CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(damagedProvider, read.ProviderKey);
        await using (read.Content) { using var bytes = new MemoryStream(); await read.Content.CopyToAsync(bytes); Assert.Equal(Bytes, bytes.ToArray()); }
        Assert.Equal(3, await fixture.Db.StorageReplicas.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairWithLostNativeAckUsesReceiptEvenAfterSourceDisappears(bool disconnect)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedAsync("yandex-disk");
        var source = await fixture.Db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "local");
        Assert.Equal(1, (await new StorageReconciliationService(fixture.Db, fixture.Registry).RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        fixture.Endpoint.FailNextPutAfterCommit = !disconnect; fixture.Endpoint.DropNextPutResponse = disconnect;
        Assert.Equal(1, (await fixture.Processor.ProcessBatchAsync("owned-native-lost-ack", CancellationToken.None)).Unknown);
        var operation = await fixture.Db.StorageOperations.AsNoTracking().SingleAsync();
        Assert.Equal(StorageOperationStatuses.Unknown, operation.Status);
        Assert.Equal(1, fixture.Endpoint.PutCount);
        Assert.Single(await fixture.Db.StorageReplicas.AsNoTracking().Where(r => r.Status == StorageReplicaStatuses.RepairPending).ToArrayAsync());
        await fixture.Local.DeleteAsync(source.Locator, CancellationToken.None);
        await fixture.Db.StorageOperations.Where(o => o.Id == operation.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));
        fixture.Db.ChangeTracker.Clear();
        Assert.Equal(1, (await fixture.Processor.ProcessBatchAsync("owned-native-receipt", CancellationToken.None)).Succeeded);
        Assert.Equal(1, fixture.Endpoint.PutCount);
        Assert.Equal(2, (await fixture.Db.StorageOperations.AsNoTracking().SingleAsync()).AttemptCount);
        Assert.Equal(1, fixture.Endpoint.PutAttempts);
        Assert.Equal(1, await fixture.Db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.RetainedCorrupt));
    }

    private static async Task<byte[]> ReadBytesAsync(IStorageProvider provider, string locator)
    {
        await using var read = await provider.OpenReadAsync(locator, CancellationToken.None);
        Assert.Equal(StorageOperationStatus.Succeeded, read.Status);
        using var bytes = new MemoryStream(); await read.Content!.CopyToAsync(bytes); return bytes.ToArray();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public static readonly byte[] DamagedBytes = Bytes.Select(b => (byte)(b ^ 1)).ToArray();
        private readonly SqliteConnection connection;
        private readonly HttpClient client;
        private readonly string root;
        public PaymentContext Db { get; }
        public LoopbackYandexDiskEndpoint Endpoint { get; }
        public YandexDiskStorageProvider Yandex { get; }
        public LocalStorageProvider Local { get; }
        public StorageProviderRegistry Registry { get; }
        public SyntheticFixedOptions<StorageOrchestrationOptions> Options { get; }
        public StorageOperationProcessor Processor { get; }
        private Fixture(SqliteConnection connection, PaymentContext db, LoopbackYandexDiskEndpoint endpoint, string root)
        {
            this.connection = connection; Db = db; Endpoint = endpoint; this.root = root;
            client = endpoint.CreateClient();
            Yandex = new(new YandexDiskService(db, client));
            Local = new(new OwnedEnvironment(root), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["StorageOrchestration:LocalRootPath"] = Path.Combine(root, "objects"), ["BackgroundServices:Enabled"] = "false", ["Email:Enabled"] = "false" }).Build());
            Registry = new([Local, Yandex]);
            Options = new(new()
            {
                Enabled = true,
                WorkerBatchSize = 20,
                ProviderConnections = new() { ["yandex-disk"] = "default" },
                DefaultPolicy = new() { Version = "owned-yandex-wire", RequiredCopies = 2, ReadProviders = ["local", "yandex-disk"], WriteProviders = ["local", "yandex-disk"] }
            });
            Processor = new(db, Registry, Options, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "pp_native_yandex_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new PaymentContext(new DbContextOptionsBuilder<PaymentContext>().UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
            db.SystemSettings.AddRange(new SystemSetting { Key = "notifications.email.enabled", Value = "false" },
                new SystemSetting { Key = "integrations.yandexDisk.oauthToken", Value = LoopbackYandexDiskEndpoint.Token },
                new SystemSetting { Key = "integrations.yandexDisk.rootPath", Value = LoopbackYandexDiskEndpoint.RootPath });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var endpoint = await LoopbackYandexDiskEndpoint.StartAsync();
            return new(connection, db, endpoint, root);
        }
        public async Task<StorageObjectGeneration> SeedAsync(string damagedProvider)
        {
            var localBytes = damagedProvider == "local" ? DamagedBytes : Bytes;
            Assert.Equal(StorageOperationStatus.Succeeded, (await Local.PutAsync(new("local.bin", new MemoryStream(localBytes), "application/octet-stream", localBytes.Length,
                Convert.ToHexString(SHA256.HashData(localBytes)).ToLowerInvariant()), CancellationToken.None)).Status);
            Endpoint.SetBytes("yandex.bin", damagedProvider == "yandex-disk" ? DamagedBytes : Bytes);
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = Bytes.Length,
                Sha256 = Hash,
                StorageObject = new StorageObject { CurrentGeneration = 1, DataClass = "document", LogicalName = "native-wire.bin" },
                Replicas = [new StorageReplica { ProviderKey = "local", Locator = "local.bin", Status = StorageReplicaStatuses.Available, SizeBytes = Bytes.Length, Sha256 = Hash },
                    new StorageReplica { ProviderKey = "yandex-disk", ConnectionId = "default", Locator = "default/yandex.bin", Status = StorageReplicaStatuses.Available, SizeBytes = Bytes.Length, Sha256 = Hash }]
            };
            Db.StorageObjectGenerations.Add(generation); await Db.SaveChangesAsync(); Db.ChangeTracker.Clear(); return generation;
        }
        public async ValueTask DisposeAsync()
        {
            client.Dispose(); await Endpoint.DisposeAsync(); await Db.DisposeAsync(); await connection.DisposeAsync();
            var resolved = Path.GetFullPath(root);
            Assert.Matches("^pp_native_yandex_[a-f0-9]{32}$", Path.GetFileName(resolved));
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(resolved));
            Directory.Delete(resolved, recursive: true);
        }
    }

    private sealed class OwnedEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PayPlanner.Api.Tests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
