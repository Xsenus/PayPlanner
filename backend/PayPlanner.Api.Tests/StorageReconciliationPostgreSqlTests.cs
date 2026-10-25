using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Services.Storage;
using Xunit;
using Xunit.Abstractions;

namespace PayPlanner.Api.Tests;

public sealed class StorageReconciliationPostgreSqlTests(ITestOutputHelper output, PostgreSqlMigratedTemplate postgresTemplate)
    : IClassFixture<PostgreSqlMigratedTemplate>
{
    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1024, true)]
    public async Task RepairCapacityGuardPreservesDamagedBytesAndOnlyQueuesAdmittedWork(long available, bool admitted)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        PostgreSqlMigratedTemplate.DatabaseLease? lease = null;
        try
        {
            lease = await postgresTemplate.CreateDatabaseAsync("pp_repair_capacity_");
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            var good = new byte[] { 1, 2, 3, 4, 5 };
            var damaged = new byte[] { 9, 9, 9, 9, 9 };
            var hash = Convert.ToHexString(SHA256.HashData(good)).ToLowerInvariant();
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = good.Length,
                Sha256 = hash,
                StorageObject = new() { DataClass = "document", OwnerType = "test", OwnerId = "42", LogicalName = "guard.bin", CurrentGeneration = 1 }
            };
            generation.Replicas.Add(new()
            {
                ProviderKey = "local",
                Locator = "source",
                Status = StorageReplicaStatuses.Available,
                SizeBytes = good.Length,
                Sha256 = hash
            });
            generation.Replicas.Add(new()
            {
                ProviderKey = "mirror",
                ConnectionId = "owned-memory",
                Locator = "damaged",
                Status = StorageReplicaStatuses.Available,
                SizeBytes = good.Length,
                Sha256 = hash
            });
            db.StorageObjectGenerations.Add(generation);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var source = new OwnedProvider("local"); source.Objects["source"] = good;
            var target = new OwnedProvider("mirror") { AvailableBytes = available }; target.Objects["damaged"] = damaged;
            var registry = new StorageProviderRegistry([source, target]);
            var monitor = new FixedOptions(new() { RepairCapacityChecksEnabled = true, RepairMinimumFreeBytes = 0 });
            var report = await new StorageReconciliationService(db, registry, monitor).RunAsync(true, CancellationToken.None);
            Assert.Equal(admitted ? 1 : 0, report.RepairOperationsQueued);
            Assert.Equal(admitted ? 0 : 1, report.IssueCountsByKind.GetValueOrDefault("capacity"));
            Assert.Equal(admitted ? 1 : 0, await db.StorageOperations.CountAsync());
            Assert.Equal(admitted ? 1 : 0, await db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.RepairPending));
            Assert.Equal(damaged, target.Objects["damaged"]);
            if (!admitted)
            {
                target.AvailableBytes = 1024;
                db.ChangeTracker.Clear();
                var retry = await new StorageReconciliationService(db, registry, monitor).RunAsync(true, CancellationToken.None);
                Assert.Equal(1, retry.RepairOperationsQueued);
                Assert.Equal(1, await db.StorageOperations.CountAsync());
            }
            output.WriteLine($"Owned PostgreSQL repair admission; admitted={admitted}");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task RepeatedLossAndCompetingReconciliationPreserveCompletedHistory(bool competing, bool corrupt, bool legacy)
    {
        var raw = Environment.GetEnvironmentVariable("PAYPLANNER_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(raw))
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "PAYPLANNER_TEST_POSTGRES must be configured in CI.");
            return;
        }
        PostgreSqlMigratedTemplate.DatabaseLease? lease = null;
        try
        {
            lease = await postgresTemplate.CreateDatabaseAsync("pp_repair_cycles_");
            var race = new BeforeSave();
            var plain = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).Options;
            var options = new DbContextOptionsBuilder<PostgreSqlPaymentContext>().UseNpgsql(lease.ConnectionString).AddInterceptors(race).Options;
            await using var db = new PostgreSqlPaymentContext(options);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var source = new OwnedProvider("local"); var target = new OwnedProvider("mirror");
            source.Objects["source"] = bytes;
            var damaged = new byte[] { 9, 9, 9, 9, 9 };
            if (corrupt) target.Objects["target"] = damaged;
            var generation = new StorageObjectGeneration
            {
                Generation = 1,
                SizeBytes = bytes.Length,
                Sha256 = hash,
                StorageObject = new() { DataClass = "document", OwnerType = "test", OwnerId = "42", LogicalName = "cycles.bin", CurrentGeneration = 1 }
            };
            generation.Replicas.Add(new() { ProviderKey = "local", Locator = "source", Status = StorageReplicaStatuses.Available, SizeBytes = bytes.Length, Sha256 = hash });
            generation.Replicas.Add(new() { ProviderKey = "mirror", ConnectionId = "owned-memory", Locator = "target", Status = StorageReplicaStatuses.Missing, SizeBytes = bytes.Length, Sha256 = hash });
            db.StorageObjectGenerations.Add(generation);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var registry = new StorageProviderRegistry([source, target]);
            var monitor = new FixedOptions(new() { Enabled = true, WorkerBatchSize = 20 });
            var processor = new StorageOperationProcessor(db, registry, monitor, TimeProvider.System, NullLogger<StorageOperationProcessor>.Instance);
            var reconcile = new StorageReconciliationService(db, registry);
            if (legacy)
            {
                db.StorageOperations.Add(new StorageOperation
                {
                    StorageObjectId = generation.StorageObjectId,
                    StorageObjectGenerationId = generation.Id,
                    OperationType = StorageOperationTypes.Repair,
                    Status = StorageOperationStatuses.Failed,
                    ProviderKey = "mirror",
                    ConnectionId = "owned-memory",
                    IdempotencyKey = $"reconcile:repair:{generation.Id:N}:{generation.Replicas.Single(r => r.ProviderKey == "mirror").Id}",
                    PayloadJson = JsonSerializer.Serialize(new { TargetLocator = "target", SourceProviderKey = "local", SourceLocator = "source" })
                });
                await db.SaveChangesAsync(); db.ChangeTracker.Clear();
                Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            }
            else Assert.Equal(1, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Equal(1, (await processor.ProcessBatchAsync("owned-pg-first-repair", CancellationToken.None)).Succeeded);
            var first = await db.StorageOperations.AsNoTracking().SingleAsync();
            Assert.Equal(StorageOperationStatuses.Succeeded, first.Status);
            var firstPayload = JsonSerializer.Deserialize<StorageOperationPayload>(first.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            if (corrupt)
            {
                Assert.Equal(damaged, target.Objects["target"]);
                Assert.Equal(bytes, target.Objects[firstPayload.TargetLocator]);
                target.Objects[firstPayload.TargetLocator] = damaged;
            }
            else target.Objects.Remove(firstPayload.TargetLocator);
            db.ChangeTracker.Clear();
            if (competing)
            {
                db.Clients.Add(new Client { Name = "Keep PostgreSQL unrelated changes" });
                race.Callback = async () =>
                {
                    await using var winner = new PostgreSqlPaymentContext(plain);
                    Assert.Equal(1, (await new StorageReconciliationService(winner, registry).RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
                };
            }
            Assert.Equal(competing ? 0 : 1, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            Assert.Equal(competing ? 1 : 0, race.Calls);
            if (competing) Assert.Single(await db.Clients.AsNoTracking().Where(c => c.Name == "Keep PostgreSQL unrelated changes").ToListAsync());
            Assert.Equal(1, (await processor.ProcessBatchAsync("owned-pg-second-repair", CancellationToken.None)).Succeeded);
            var second = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id != first.Id);
            var secondPayload = JsonSerializer.Deserialize<StorageOperationPayload>(second.PayloadJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(bytes, target.Objects[secondPayload.TargetLocator]);
            if (corrupt)
            {
                Assert.NotEqual(firstPayload.TargetLocator, secondPayload.TargetLocator);
                Assert.Equal(damaged, target.Objects["target"]);
                Assert.Equal(damaged, target.Objects[firstPayload.TargetLocator]);
                Assert.Equal(2, await db.StorageReplicas.CountAsync(r => r.Status == StorageReplicaStatuses.RetainedCorrupt));
            }
            var retained = await db.StorageOperations.AsNoTracking().SingleAsync(o => o.Id == first.Id);
            Assert.Equal(first.CompletedAt, retained.CompletedAt);
            Assert.Equal(first.AttemptCount, retained.AttemptCount);
            Assert.Equal(first.UpdatedAt, retained.UpdatedAt);
            Assert.Equal(2, await db.StorageOperations.CountAsync(o => o.Status == StorageOperationStatuses.Succeeded));
            Assert.Equal(0, (await reconcile.RunAsync(true, CancellationToken.None)).RepairOperationsQueued);
            output.WriteLine($"Owned PostgreSQL database executed two real repair cycles; competing={competing}; corrupt={corrupt}; legacy={legacy}");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private sealed class BeforeSave : SaveChangesInterceptor
    {
        public Func<Task>? Callback { get; set; }
        public int Calls { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Callback is { } action) { Callback = null; Calls++; await action(); } return result; }
    }
    private sealed class FixedOptions(StorageOrchestrationOptions value) : IOptionsMonitor<StorageOrchestrationOptions>
    {
        public StorageOrchestrationOptions CurrentValue => value;
        public StorageOrchestrationOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<StorageOrchestrationOptions, string?> listener) => null;
    }
    private sealed class OwnedProvider(string key) : IStorageProvider, IStorageRepairDestinationProvider, IStorageCapacityProvider
    {
        public string Key => key;
        public long AvailableBytes { get; set; } = long.MaxValue / 4;
        public Task<StorageCapacityResource> ObserveCapacityAsync(string resourceKey, string? connectionId,
            long minimumFreeBytes, CancellationToken ct)
            => Task.FromResult(new StorageCapacityResource(resourceKey, "test-" + key,
                AvailableBytes < 0 ? null : AvailableBytes,
                minimumFreeBytes, DateTimeOffset.UtcNow));
        public string CreateRepairLocator(string originalLocator, Guid generationId, string idempotencyKey)
            => $"owned-recovery/{generationId:N}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))).ToLowerInvariant()}.bin";
        public Dictionary<string, byte[]> Objects { get; } = new();
        public StorageProviderCapabilities Capabilities { get; } = new(true, false, true, false, false, false, false, false, false);
        public async Task<StorageWriteResult> PutAsync(StorageWriteRequest request, CancellationToken ct)
        {
            if (Objects.ContainsKey(request.Locator)) return new(StorageOperationStatus.Conflict, request.Locator, 0, null);
            using var content = new MemoryStream(); await request.Content.CopyToAsync(content, ct);
            var bytes = content.ToArray(); Objects[request.Locator] = bytes;
            return new(StorageOperationStatus.Succeeded, request.Locator, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }
        public Task<StorageObjectMetadata> HeadAsync(string locator, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(locator, out var bytes)
                ? new StorageObjectMetadata(StorageOperationStatus.Succeeded, locator, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), null)
                : new StorageObjectMetadata(StorageOperationStatus.NotFound, locator, 0, null, null));
        public Task<StorageReadResult> OpenReadAsync(string locator, CancellationToken ct)
            => Task.FromResult(Objects.TryGetValue(locator, out var bytes)
                ? new StorageReadResult(StorageOperationStatus.Succeeded, locator, new MemoryStream(bytes, false), bytes.Length)
                : new StorageReadResult(StorageOperationStatus.NotFound, locator, null, 0));
        public Task<StorageDeleteResult> DeleteAsync(string locator, CancellationToken ct)
            => Task.FromResult(new StorageDeleteResult(Objects.Remove(locator) ? StorageOperationStatus.Succeeded : StorageOperationStatus.NotFound, locator));
    }
}
