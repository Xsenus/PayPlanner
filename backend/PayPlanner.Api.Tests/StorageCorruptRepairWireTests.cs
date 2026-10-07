using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services;
using PayPlanner.Api.Services.Storage;
using Xunit;

namespace PayPlanner.Api.Tests;

public sealed class StorageCorruptRepairWireTests
{
    [Fact]
    public async Task NativeS3ReadRejectsChangedBytesAndFallsBackToLocal()
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(secondCopy: endpoint);
        await using var db = runtime.OpenDatabase();
        Assert.Equal("false", (await db.SystemSettings.SingleAsync(s => s.Key == "notifications.email.enabled")).Value);
        var monitor = new SyntheticFixedOptions<StorageOrchestrationOptions>(runtime.Configuration
            .GetSection(StorageOrchestrationOptions.SectionName).Get<StorageOrchestrationOptions>()!);
        monitor.CurrentValue.DefaultPolicy.ReadProviders = ["s3", "local"];
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(runtime.Root, "keys")),
            b => b.SetApplicationName(Path.GetFileName(runtime.Root)));
        var s3 = new S3StorageProvider(new S3StorageService(db, new S3StorageClientFactory(runtime.Configuration), protection,
            new S3EndpointSecurityPolicy(new SyntheticFixedOptions<S3EndpointSecurityOptions>(new()
            { AllowHttp = true, AllowedHosts = ["127.0.0.1"] }))));
        var registry = new StorageProviderRegistry([new LocalStorageProvider(new OwnedEnvironment(runtime.Root), runtime.Configuration), s3]);
        var storage = new StorageObjectService(db, new StoragePolicyService(monitor, registry), registry, monitor);
        var original = Encoding.UTF8.GetBytes("Проверенная копия для скачивания");
        var result = await storage.WriteAsync(new("document", "test", "owned", "native-read.txt", "text/plain",
            new MemoryStream(original), "owned-native-s3-read"), CancellationToken.None);
        Assert.Equal(1, (await new StorageOperationProcessor(db, registry, monitor, TimeProvider.System,
            NullLogger<StorageOperationProcessor>.Instance).ProcessBatchAsync("owned-native-s3-copy", CancellationToken.None)).Succeeded);
        var s3Replica = await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.ProviderKey == "s3");
        var changed = original.ToArray(); changed[0] ^= 1;
        Assert.Equal(StorageOperationStatus.Succeeded, (await s3.PutAsync(new(s3Replica.Locator,
            new MemoryStream(changed), "text/plain", changed.Length,
            Convert.ToHexString(SHA256.HashData(changed)).ToLowerInvariant(), Overwrite: true), CancellationToken.None)).Status);

        await using (var read = await storage.OpenReadAsync(result.ObjectId, CancellationToken.None))
        {
            Assert.NotNull(read);
            Assert.Equal("local", read.ProviderKey);
            using var bytes = new MemoryStream();
            await read.Content.CopyToAsync(bytes);
            Assert.Equal(original, bytes.ToArray());
        }
        Assert.Equal(StorageReplicaStatuses.Suspect,
            (await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == s3Replica.Id)).Status);
        Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), $"pp_verified_read_{result.ObjectId:N}_*.tmp"));
    }

    [Theory]
    [InlineData("local", false)]
    [InlineData("s3", false)]
    [InlineData("local", true)]
    [InlineData("s3", true)]
    public async Task RealLocalAndAwsRepairRetainDamagedBytesAndReadVerifiedReplacement(string damagedProvider, bool legacy)
    {
        await using var endpoint = await LoopbackS3Endpoint.StartAsync();
        // This runner owns a fresh SQLite DB, local object/key roots and loopback-only
        // S3 credentials. Background workers/email and the database email flag are off.
        await using var runtime = await SyntheticWritableBackendProcess.StartAsync(secondCopy: endpoint);
        await using var db = runtime.OpenDatabase();
        Assert.Equal("false", (await db.SystemSettings.SingleAsync(s => s.Key == "notifications.email.enabled")).Value);
        var monitor = new SyntheticFixedOptions<StorageOrchestrationOptions>(runtime.Configuration
            .GetSection(StorageOrchestrationOptions.SectionName).Get<StorageOrchestrationOptions>()!);
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(runtime.Root, "keys")),
            b => b.SetApplicationName(Path.GetFileName(runtime.Root)));
        var service = new S3StorageService(db, new S3StorageClientFactory(runtime.Configuration), protection,
            new S3EndpointSecurityPolicy(new SyntheticFixedOptions<S3EndpointSecurityOptions>(new()
            { AllowHttp = true, AllowedHosts = ["127.0.0.1"] })));
        var local = new LocalStorageProvider(new OwnedEnvironment(runtime.Root), runtime.Configuration);
        var registry = new StorageProviderRegistry([local, new S3StorageProvider(service)]);
        var policies = new StoragePolicyService(monitor, registry);
        var storage = new StorageObjectService(db, policies, registry, monitor);
        var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
        var bytes = Encoding.UTF8.GetBytes("Owned actual repair bytes: Документ");
        var write = await storage.WriteAsync(new("document", "test", "owned", "repair-wire.txt", "text/plain",
            new MemoryStream(bytes), "owned-wire-repair"), CancellationToken.None);
        Assert.Equal(1, (await processor.ProcessBatchAsync("owned-wire-replication", CancellationToken.None)).Succeeded);
        var originals = await db.StorageReplicas.AsNoTracking().Where(r => r.StorageObjectGeneration.StorageObjectId == write.ObjectId).ToArrayAsync();
        var original = Assert.Single(originals, r => r.ProviderKey == damagedProvider);
        var source = Assert.Single(originals, r => r.ProviderKey != damagedProvider);
        var damaged = bytes.ToArray(); damaged[0] ^= 1;
        var target = registry.GetRequired(damagedProvider);
        Assert.Equal(StorageOperationStatus.Succeeded, (await target.PutAsync(new(original.Locator, new MemoryStream(damaged),
            "text/plain", damaged.Length, Convert.ToHexString(SHA256.HashData(damaged)).ToLowerInvariant(), Overwrite: true), CancellationToken.None)).Status);
        db.ChangeTracker.Clear();
        var reconcile = new StorageReconciliationService(db, registry);
        if (legacy)
        {
            db.StorageOperations.Add(new StorageOperation
            {
                StorageObjectId = write.ObjectId,
                StorageObjectGenerationId = original.StorageObjectGenerationId,
                OperationType = StorageOperationTypes.Repair,
                Status = StorageOperationStatuses.Failed,
                IdempotencyKey = $"reconcile:repair:{original.StorageObjectGenerationId:N}:{original.Id}",
                ProviderKey = original.ProviderKey,
                ConnectionId = original.ConnectionId,
                PayloadJson = JsonSerializer.Serialize(new { TargetLocator = original.Locator, SourceProviderKey = source.ProviderKey, SourceLocator = source.Locator })
            });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
        else Assert.Equal(1, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
        var job = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.OperationType == StorageOperationTypes.Repair);
        var plan = JsonSerializer.Deserialize<StorageOperationPayload>(job.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(legacy ? null : original.Locator, plan.OriginalTargetLocator);
        Assert.Equal(1, (await processor.ProcessBatchAsync("owned-wire-repair", CancellationToken.None)).Succeeded);
        var completed = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == job.Id);
        Assert.Equal(job.IdempotencyKey, completed.IdempotencyKey);
        plan = JsonSerializer.Deserialize<StorageOperationPayload>(completed.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(original.Locator, plan.OriginalTargetLocator);
        Assert.Equal(StorageReplicaStatuses.RetainedCorrupt, (await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Id == original.Id)).Status);
        Assert.Equal(StorageReplicaStatuses.Available, (await db.StorageReplicas.AsNoTracking().SingleAsync(r => r.Locator == plan.TargetLocator)).Status);
        await using (var old = await target.OpenReadAsync(original.Locator, CancellationToken.None))
        {
            using var received = new MemoryStream(); await old.Content!.CopyToAsync(received);
            Assert.Equal(damaged, received.ToArray());
        }
        var settled = await reconcile.RunAsync(true, CancellationToken.None);
        Assert.Equal(2, settled.HealthyReplicas);
        Assert.Equal(0, settled.RepairOperationsQueued);
        Assert.Contains(settled.Issues, i => i.Kind == StorageReplicaStatuses.RetainedCorrupt);
        // Exclude the original good source from reader selection without deleting it.
        // The separate processor test covers physical source loss after a lost ACK.
        await db.StorageReplicas.Where(r => r.Id == source.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, StorageReplicaStatuses.Missing));
        var read = await storage.OpenReadAsync(write.ObjectId, CancellationToken.None);
        Assert.NotNull(read);
        await using (read.Content)
        {
            using var received = new MemoryStream(); await read.Content.CopyToAsync(received);
            Assert.Equal(bytes, received.ToArray());
        }
        Assert.Equal(damagedProvider, read.ProviderKey);
        Assert.Equal(3, await db.StorageReplicas.CountAsync(r => r.StorageObjectGeneration.StorageObjectId == write.ObjectId));
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
